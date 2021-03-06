﻿//    Cekirdekler API: a C# explicit multi-device load-balancer opencl wrapper
//    Copyright(C) 2017 Hüseyin Tuğrul BÜYÜKIŞIK

//   This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//    GNU General Public License for more details.

//    You should have received a copy of the GNU General Public License
//    along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClObject;
using Cekirdekler.ClArrays;
using Cekirdekler.Hardware;

namespace Cekirdekler
{
    /// <summary>
    /// <para>main part of the API. Binds devices to platforms, buffers to arrays, commandqueues to commands</para>
    /// <para>can pipeline kernel as many sub works that are issued concurrently</para>
    /// <para>can load-balance between gpus, cpus and accelerators</para>
    /// </summary>
    public class Cores
    {
        [DllImport("KutuphaneCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern void setKernelArguments(IntPtr hKernel, IntPtr hBuffer, int index_);

        [DllImport("KutuphaneCL", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr platformList();

        [DllImport("KutuphaneCL", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int numberOfPlatforms(IntPtr hList);

        [DllImport("KutuphaneCL", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void deletePlatformList(IntPtr hList);

        int localRange = 256; // just a default local thread number for all devices

        List<ClPlatform> platforms = new List<ClPlatform>();

        ClString kernels = null;
        internal Worker[] workers = null;
        ClString[] kernelNames = null;
        private int errorNotification;

        private bool enqueueModePrivate = false;

        private bool enqueueModeAsyncEnablePrivate = false;

        private int enqueueModeAsyncQueueIndex = 0;

        /// <summary>
        /// <para>just to upload/download data on GPU without any compute operation</para>
        /// <para>specifically used for single gpu pipelining with enqueue mode for input-output stages overlapping</para>
        /// <para>not for driver/event pipelining </para>
        /// <para>with or without multiple gpus, it skips compute part and directly does data transmissions</para>
        /// </summary>
        public bool noComputeMode { get; set; }

        /// <summary>
        /// <para>only for single gpu(or device to device pipeline stages)  and not for driver/event pipelining </para>
        /// <para>used by enqueueMode to distribute each compute job to a different queue or not</para>
        /// <para>true=distribute each compute to a different queue</para>
        /// <para>false=use single queue for all jobs</para>
        /// </summary>
        public bool enqueueModeAsyncEnable
        {
            get { return enqueueModeAsyncEnablePrivate; }
            set { enqueueModeAsyncEnablePrivate = value; if(value) enqueueModeAsyncQueueIndex++; }
        }

        /// <summary>
        /// <para>only for single gpu (or device to device pipeline stages) and not for driver/event pipelining </para>
        /// <para>not usable with host-device pipeline</para>
        /// <para>kernel-array matrix must not change during in enqueue mode(setting kernel arguments are not "enqueue" command)</para>
        /// <para>arrays from host must not change during enqueue mode</para>
        /// <para>true=disables commandQueue synchronization</para>
        /// <para>false=enables commandQueue synchronization again and setting to false synchronizes host-device immediately</para>
        /// </summary>
        public bool enqueueMode {

            get { return enqueueModePrivate; }

            set {
                if(value && !enqueueModePrivate)
                {
                    // starting a new enqueue mode and its benchmarking
                    for(int i=0;i<workers.Length;i++)
                        workers[i].startBench();
                }


                bool tmp = false;
                tmp = enqueueModePrivate;
                enqueueModePrivate = value;
                if (!value)
                {
                    Parallel.For(0, workers.Length, i => {
                        workers[i].finishUsedComputeQueues();
                        if ((!value) && tmp)
                        {
                            // ending current enqueue mode and its benchmarking
                                workers[i].endBench(lastUsedComputeId);
                        }
                    });
                }
                else
                {
                    enqueueModeAsyncQueueIndex = 0;
                }
            }
        }
        /// <summary>
        /// global range values for opencl per device per compute id
        /// </summary>
        public Dictionary<int, int[]> globalRanges = null;

        /// <summary>
        /// global reference points for ranges for opencl per device per compute id
        /// </summary>
        public Dictionary<int, int[]> globalReferences = null;

        /// <summary>
        /// contains C99 kernel compiler errors returned from all devices
        /// </summary>
        public string allErrorsString { get; set; }

        private Thread[] workerThreads = null;

        /// <summary>
        /// Main class for scheduling device queues and controlling work distributions
        /// </summary>
        /// <param name="deviceTypesToUse">"cpu" "gpu" "cpu gpu" "gpu acc" ...</param>
        /// <param name="kernelFileString"></param>
        /// <param name="kernelFunctionNamesInKernelFileString"></param>
        /// <param name="defaultQueue">OpenCL dynamic parallelism queue</param>
        /// <param name="localRangeDeprecated"></param>
        /// <param name="numGPUToUse">if pc has 4 gpu, can set this to 4</param>
        /// <param name="MAX_CPU">-1 = MAX - 1, max( min(MAX_CPU,MAX-1),1) </param>
        /// <param name="GPU_STREAM">default is true: map - unmap instead of extra read-write for all devices</param>
        /// <param name="noPipelining">disables allocation of abundant command queues(can't enable driver-driver pipelining later)</param>
        public Cores(string deviceTypesToUse, string kernelFileString, string[] kernelFunctionNamesInKernelFileString,bool defaultQueue,
                            int localRangeDeprecated = 256, int numGPUToUse = -1, bool GPU_STREAM = true, int MAX_CPU = -1, bool noPipelining=false)
        {
            localRange = localRangeDeprecated;
            IntPtr handlePlatformList = platformList();
            int numberOfAllPlatformsThatAreOpenCL12Capable = numberOfPlatforms(handlePlatformList);
            Dictionary<ClPlatform, List<ClDevice>> selectedDevicesForGPGPU = new Dictionary<ClPlatform, List<ClDevice>>();

            // should have used toLower()
            if (deviceTypesToUse.Contains("cpu") || deviceTypesToUse.Contains("CPU") || deviceTypesToUse.Contains("Cpu"))
            {
                for (int i = 0; i < numberOfAllPlatformsThatAreOpenCL12Capable; i++)
                {
                    ClPlatform p = new ClPlatform(handlePlatformList, i);

                    int numberOfProcessors = p.numberOfCpus();
                    if (numberOfProcessors > 0)
                    {
                        if (!selectedDevicesForGPGPU.ContainsKey(p))
                            selectedDevicesForGPGPU.Add(p, new List<ClDevice>());

                        for (int j = 0; j < numberOfProcessors; j++)
                            selectedDevicesForGPGPU[p].Add(new ClDevice(p, ClPlatform.CODE_CPU(), j, 
                                true /* true=selecting necessary number of cores of device */, 
                                true /* true=directly access RAM, no extra buffer copies */, 
                                MAX_CPU));
                    }
                }
            }

            // should have used toLower()
            if (numGPUToUse != 0 && deviceTypesToUse.Contains("gpu") || deviceTypesToUse.Contains("GPU") || deviceTypesToUse.Contains("Gpu"))
            {
                int tmpGPU = 0;
                for (int i = 0; i < numberOfAllPlatformsThatAreOpenCL12Capable; i++)
                {
                    ClPlatform p = new ClPlatform(handlePlatformList, i);

                    int sayi = p.numberOfGpus();
                    if (numGPUToUse != -1)
                        sayi = Math.Min(sayi, numGPUToUse);
                    if (sayi > 0 && (tmpGPU < numGPUToUse || numGPUToUse == -1))
                    {
                        if (!selectedDevicesForGPGPU.ContainsKey(p))
                            selectedDevicesForGPGPU.Add(p, new List<ClDevice>());

                        for (int j = 0; j < sayi; j++)
                        {
                            selectedDevicesForGPGPU[p].Add(new ClDevice(p, ClPlatform.CODE_GPU(), j, false, GPU_STREAM, -1));
                            tmpGPU++;
                        }
                    }
                }
            }

            // should have used toLower()
            if (deviceTypesToUse.Contains("acc") || deviceTypesToUse.Contains("ACC") || deviceTypesToUse.Contains("Acc"))
            {
                for (int i = 0; i < numberOfAllPlatformsThatAreOpenCL12Capable; i++)
                {
                    ClPlatform p = new ClPlatform(handlePlatformList, i);

                    int sayi = p.numberOfAccelerators();
                    if (sayi > 0)
                    {
                        if (!selectedDevicesForGPGPU.ContainsKey(p))
                            selectedDevicesForGPGPU.Add(p, new List<ClDevice>());

                        for (int j = 0; j < sayi; j++)
                            selectedDevicesForGPGPU[p].Add(new ClDevice(p, ClPlatform.CODE_ACC(), j, false, GPU_STREAM, -1));

                    }
                }
            }

            deletePlatformList(handlePlatformList);
            allErrorsString = "";
            errorNotification = 0;

            if ((selectedDevicesForGPGPU.Keys == null) || (selectedDevicesForGPGPU.Keys.ToArray().Length < 1))
            {
                allErrorsString += "No OpenCL-capable device was found." + Environment.NewLine;
                errorNotification++;
                return;
            }

            kernels = new ClString(kernelFileString);
            kernelNames = new ClString[kernelFunctionNamesInKernelFileString.Length];
            globalRanges = new Dictionary<int, int[]>();
            globalReferences = new Dictionary<int, int[]>();
            for (int i = 0; i < kernelNames.Length; i++)
            {
                kernelNames[i] = new ClString(kernelFunctionNamesInKernelFileString[i]);
            }


            List<Worker> tmp = new List<Worker>();

            int numberOfWorkers = 0;
            platforms = selectedDevicesForGPGPU.Keys.ToList();
            for (int i = 0; i < platforms.Count; i++)
            {
                numberOfWorkers = selectedDevicesForGPGPU[platforms[i]].Count;
                for (int j = 0; j < numberOfWorkers; j++)
                    tmp.Add(new Worker(selectedDevicesForGPGPU[platforms[i]][j], kernels, kernelNames, defaultQueue,16, noPipelining));
            }
            workers = tmp.ToArray();
            workerThreads = new Thread[workers.Length];
            for (int i = 0; i < workers.Length; i++)
            {
                errorNotification += workers[i].getErrorCode();
                if (workers[i].getErrorCode() != 0)
                {
                    Console.WriteLine("error!");
                    allErrorsString += workers[i].getAllErrors() + Environment.NewLine + "*******************" + Environment.NewLine;
                }
            }
        }


        internal ClCommandQueue lastUsedCommandQueueOfFirstDevice()
        {
            return workers[0].lastUsedComputeQueue();
        }

        /// <summary>
        /// Main class for scheduling device queues and controlling work distributions
        /// </summary>
        /// <param name="devicesForGPGPU">device or a group of devices to use in gpgpu calculations</param>
        /// <param name="kernelFileString"></param>
        /// <param name="kernelFunctionNamesInKernelFileString"></param>
        /// <param name="defaultQueue">OpenCL 2.0 dynamic parallelism queue</param>
        /// <param name="noPipelining">disables allocation of abundant command queues(can't enable driver-driver pipelining later)</param>
        /// <param name="computeQueueConcurrency">max number of command queues to send commands asynchronously(max=16,min=1)</param>
        public Cores(ClDevices devicesForGPGPU, string kernelFileString, string[] kernelFunctionNamesInKernelFileString, bool defaultQueue, int computeQueueConcurrency = 16, bool noPipelining=false)
        {
            localRange = 256;
            Dictionary<ClPlatform, List<ClDevice>> selectedDevicesForGPGPU = new Dictionary<ClPlatform, List<ClDevice>>();
            allErrorsString = "";
            errorNotification = 0;

            if ((devicesForGPGPU==null) || (devicesForGPGPU.Length<1) )
            {
                allErrorsString += "No OpenCL-capable device was found." + Environment.NewLine;
                errorNotification++;
                return;
            }


            for (int i = 0; i < devicesForGPGPU.devices.Length; i++)
            {
                ClPlatform tmpPlatform = devicesForGPGPU.devices[i].clPlatformForCopy;
                if (!selectedDevicesForGPGPU.ContainsKey(tmpPlatform))
                    selectedDevicesForGPGPU.Add(tmpPlatform, new List<ClDevice>());
                
                    selectedDevicesForGPGPU[tmpPlatform].Add(devicesForGPGPU.devices[i]);
            }
            kernels = new ClString(kernelFileString);
            kernelNames = new ClString[kernelFunctionNamesInKernelFileString.Length];
            globalRanges = new Dictionary<int, int[]>();
            globalReferences = new Dictionary<int, int[]>();
            for (int i = 0; i < kernelNames.Length; i++)
            {
                kernelNames[i] = new ClString(kernelFunctionNamesInKernelFileString[i]);
            }


            List<Worker> tmp = new List<Worker>();

            int numberOfWorkers = 0;
            platforms = selectedDevicesForGPGPU.Keys.ToList();
            for (int i = 0; i < platforms.Count; i++)
            {
                numberOfWorkers = selectedDevicesForGPGPU[platforms[i]].Count;
                for (int j = 0; j < numberOfWorkers; j++)
                    tmp.Add(new Worker(selectedDevicesForGPGPU[platforms[i]][j], kernels, kernelNames, defaultQueue, computeQueueConcurrency, noPipelining));
            }
            workers = tmp.ToArray();
            workerThreads = new Thread[workers.Length];
            for (int i = 0; i < workers.Length; i++)
            {
                errorNotification += workers[i].getErrorCode();
                if (workers[i].getErrorCode() != 0)
                {
                    Console.WriteLine("error!");
                    allErrorsString += workers[i].getAllErrors() + Environment.NewLine + "*******************" + Environment.NewLine;
                }
            }
        }


        /// <summary>
        /// 1 worker per device (each worker can have concurrent pipelining if enabled)
        /// </summary>
        /// <returns></returns>
        public int numberOfDevices()
        {
            return workers.Length;
        }

        /// <summary>
        /// error description from compiler
        /// </summary>
        /// <returns></returns>
        public string errorMessage()
        {
            return allErrorsString;
        }

        /// <summary>
        /// different from zero means error
        /// </summary>
        /// <returns></returns>
        public int errorCode()
        {
            return errorNotification;
        }

        /// <summary>
        /// pin C# arrays in place for compute so they don't move while computing, doesn't touch C++ arrays 
        /// </summary>
        /// <param name="arr"></param>
        /// <returns></returns>
        GCHandle? pinArray(object arr)
        {

            if (
                arr.GetType() == typeof(float[]) ||
                arr.GetType() == typeof(double[]) ||
                arr.GetType() == typeof(long[]) ||
                arr.GetType() == typeof(int[]) ||
                arr.GetType() == typeof(char[]) ||
                arr.GetType() == typeof(uint[]) ||
                arr.GetType() == typeof(byte[])
              )
                return GCHandle.Alloc(arr, GCHandleType.Pinned);
            else
            {
                // no need to lock non-C# arrays
                return null;
            }
        }

        void unpinArray(GCHandle? handlePinArray)
        {
            handlePinArray.Value.Free();
        }


        private int ONLY_READ = 0;
        private int ONLY_WRITE = 0;

        /// <summary>
        /// <para>a network of opencl events controls multiple commandqueues so read is done concurrently with compute and write</para>
        /// <para>hides the least time consuming part(read or compute or write) latency behind the most time consuming part(read or compute or write)</para>
        /// <para>2 command queues issue read(from array) operations</para>
        /// <para>2 command queues issue compute operations</para>
        /// <para>2 command queues issue write(to array) operations</para>
        /// <para>pipeline parts mush be minimum 4 and be multiple of 4</para>
        /// </summary>
        public const bool PIPELINE_EVENT = true;

        /// <summary>
        /// <para>no opencl event is used. the driver controls scheduling for the optimum multi-commandqueue work overlapping</para>
        /// <para>16 command queues are used, each one has (read + compute + write) of a small portion of whole work</para>
        /// <para>pipeline parts mush be minimum 4 and be multiple of 4</para>
        /// </summary>
        public const bool PIPELINE_DRIVER = false;

        private bool smooth = true;
        /// <summary>
        /// smoothing of load balancing to take care of performance spikes and OS hiccups and similar
        /// </summary>
        public bool smoothLoadBalancer { get { return smooth; } set { smooth = value; } }

        int counterAffinity = 0;

        // a message for enqueue mode transition methods to sync on extra command queues
        // ClNumberCruncher will be using this
        internal bool concurrentKernelExecutionInSameDevice = false;
        internal int numConcurrentKernelExecutionInSameDevice = 0;

        // for load balancer
        internal int[] tmpGlobalRanges;
        internal double[] tmpThroughputs;

        /// <summary>
        /// <para>true = a marker is added to the used command queue and a callback increments a counter for that command queue</para>
        /// <para>total count is queried by countMarkers()</para>
        /// <para>total reached markers are queried by countMarkerCallbacks()</para>
        /// <para>so the remaining markers are countMarkers() - countMarkerCallbacks()</para>
        /// <para>has performance penalty for many repeated light workload kernels (2-3 microseconds gap becomes 200-300 microseconds)</para>
        /// </summary>
        public bool fineGrainedQueueControl { get; set; }

        // to protect unmanaged arrays being garbage collected before its opencl buffer is released
        // in a LRU manner(later will be implemented)
        private Dictionary<object,bool> strongReferences { get; set; }
        private List<object> strongReferencesList { get; set; }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="kernelNames">names of kernels to be executed consecutively</param>
        /// <param name="numRepeats">repeats(0=no repeat, 1=once, n=n times) kernels with a sync kernel at the end of each repeat step</param>
        /// <param name="syncKernelName">after n different kernels, a sync kernel is executed if numRepeats>0 </param>
        /// <param name="arrs">C#(float[],int[],...) or C++ arrays(FastArr, ClArray)</param>
        /// <param name="readWrite">"partial read": each device reads its own territory(possibly pipelined), "read": all devices read all at once,"write": all devices write their own results(possibly pipelined),"write all": only single device writes all arrays at once, without checking size</param>
        /// <param name="elementsPerWorkItem">number of array elements that each workitem alters,reads or writes(if kernel alters randomly,using "partial" (in readWrite) is undefined behaviour)</param>
        /// <param name="globalRange">total workitems to be distributed to all selected devices</param>
        /// <param name="computeId">compute id value, determines that this operation is similar to or different than any other compute(to distribute workitems better iteratively)</param>
        /// <param name="globalOffset">(shifts all workitem right by this number(for cluster extension))first device in load-balance always starts from zero unless this variable is set to x>0 </param>
        /// <param name="pipelineEnabled">enables multi-commandqueue compute to hide latencies. example: if read takes %33, compute takes %33, write takes %33 of time, then pipelining makes it 3x as fast</param>
        /// <param name="numberOfPipelineStages">minimum 4, multiple of 4</param>
        /// <param name="pipelineType">PIPELINE_EVENT(6 queues, read-write-compute separated) or PIPELINE_DRIVER(16 queues, read-compute-write together)</param>
        /// <param name="localRange">local range value (number of workitems per group) for all devices</param>
        public void compute(string[] kernelNames, int numRepeats, string syncKernelName, object[] arrs, string[] readWrite,
                                int[] elementsPerWorkItem, int globalRange, int computeId, int globalOffset = 0,
                                bool pipelineEnabled = false, int numberOfPipelineStages = 4, bool pipelineType = PIPELINE_EVENT, int localRange = 256)
        {
            // keep garbage collector out
            if (strongReferencesList == null)
                strongReferencesList = new List<object>();
            if (strongReferences == null)
                strongReferences = new Dictionary<object, bool>();
            int arrLengthStrongReference = arrs.Length;
            for (int i = 0; i < arrLengthStrongReference; i++)
            {
                if (strongReferences.ContainsKey(arrs[i]))
                {

                }
                else
                {
                    strongReferences.Add(arrs[i], true);
                    strongReferencesList.Add(arrs[i]);
                    if (strongReferencesList[i] is IBufferOptimization)
                        strongReferencesList.Add(((IBufferOptimization)arrs[i]).array);

                }
            }
            // keep garbage collector out end

            if (errorCode() != 0)
            {
                Console.WriteLine("Cores class compute error.");
                Console.WriteLine(errorMessage());
                return;
            }
            counterAffinity++;
            this.localRange = localRange;
            for (int i = 0; i < arrs.Length; i++)
            {
                if (arrs[i] is IBufferOptimization)
                {
                    arrs[i] = ((IBufferOptimization)arrs[i]).array;
                }
            }


            // not only adds device fission to limit cpu usage, but also alters processor affinity too.
            // lets at least 1 thread free for other processes (and drivers)
            // if cpu has some problems, try cancelling device fission on cpu
            // runs this once for every 255 executions 
            if (counterAffinity % 255 == 1)
            {
                int numThreads = Environment.ProcessorCount;
                if (numThreads <= 0)
                    numThreads = 1;
                int one = 1;
                int t = 1;
                for (int ii = 1; ii < numThreads; ii++)
                    t += (one << ii);
                if (t <= 0)
                    t = 1;
                Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)(t);
            }

            bool first = true;

            List<GCHandle?> pinnedArrays = new List<GCHandle?>();
            for (int i = 0; i < arrs.Length; i++)
            {

                if (!Functions.isTypeOfFastArr(arrs[i]))
                    pinnedArrays.Add(pinArray(arrs[i]));
            }


            int[] selectedGlobalRanges = null;
            int[] selectedGlobalReferences = null;
            if (globalRanges.ContainsKey(computeId))
            {
                selectedGlobalRanges = globalRanges[computeId];
                selectedGlobalReferences = globalReferences[computeId];
            }
            else
            {
                globalRanges.Add(computeId, new int[workers.Length]);
                selectedGlobalRanges = globalRanges[computeId];
                globalReferences.Add(computeId, new int[workers.Length]);
                selectedGlobalReferences = globalReferences[computeId];
            }

            for (int i = 0; i < workers.Length; i++)
            {

                if (selectedGlobalRanges[i] != 0)
                {
                    first = false;
                    break;
                }
            }

            if (first)
            {
                bool b1 = true;
                int tryA = 0;
                for (int i = 0; i < workers.Length; i++)
                {
                    selectedGlobalRanges[i] = globalRange / workers.Length;
                    tryA += selectedGlobalRanges[i];
                }

                if (tryA != globalRange)
                    selectedGlobalRanges[0] += (globalRange - tryA);

                for (int i = 0; i < workers.Length; i++)
                {
                    b1 &= (selectedGlobalRanges[i] >= (numberOfPipelineStages * this.localRange));
                }


                double[] benchmarkInitVal = new double[workers.Length];
                for (int l = 0; l < workers.Length; l++)
                {
                    benchmarkInitVal[l] = 10;
                }


                Functions.loadBalance(benchmarkInitVal, smooth, performanceHistory(computeId), globalRange, selectedGlobalRanges, ((b1 & pipelineEnabled & (globalRange >= (numberOfPipelineStages * this.localRange))) ? numberOfPipelineStages * this.localRange : this.localRange), this);
            }
            else
            {
                bool b1 = true;
                for (int i = 0; i < workers.Length; i++)
                {
                    b1 &= (selectedGlobalRanges[i] >= (numberOfPipelineStages * this.localRange));
                }
                Functions.loadBalance(benchmarks(computeId), smooth, performanceHistory(computeId), globalRange, selectedGlobalRanges, ((b1 & pipelineEnabled & (globalRange >= (numberOfPipelineStages * this.localRange))) ? numberOfPipelineStages * this.localRange : this.localRange), this);
            }

            int totalGlobalRanges = 0;
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i].benchmark0(computeId);
                selectedGlobalReferences[i] = totalGlobalRanges + globalOffset;
                totalGlobalRanges += selectedGlobalRanges[i];
            }
            object lockCl = new object();



            // one of the conditions to apply pipelining
            int counterCl = 0;


            // repeating stops pipelining. pipelining only works for divisible kernels
            //if (syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1 )
            if (numRepeats > 1)
                counterCl++;

            int pipelineBlobs = numberOfPipelineStages;


            bool case1 = true;


            // all to-be-copied arrays must be exactly divisible by pipelineBlobs*localRange
            // example: if there are 64 blobs and if local range is 64, 
            //          then any device must have multiple of 4096 workitems so they can pipeline

            // no pipelining = best load balance
            // less pipelining = fine grained load balance  (such as only 4-8 blobs)
            // more pipelining = better latency hiding (such as 32-128 blobs)
            // even more pipelining = diminishing returns and cpu bottlenecks
            // less local range = fine grained load balance

            for (int i = 0; i < workers.Length; i++)
            {
                // this should be enough
                case1 &= (((selectedGlobalRanges[i] / pipelineBlobs) % this.localRange) == 0);

                // trivial?(excludes "zero" result)
                case1 &= (selectedGlobalRanges[i] >= this.localRange * pipelineBlobs);
            }

            if (counterCl == 0 && pipelineEnabled && case1)
            {
                // pipeline is on

                // 1: do all non-partial reads
                // 2: start partials 
                // 3: queueForRead: read1    read2    reaad3    read4  
                // 4: queueForComp:   x      compute1 compute2  compute3  compute4
                // 5: queueForWrit:   x        x      write1    write2    write3
                // or
                // 3: queue-1: read compute write read compute write
                // 4: queue-2: read   compute write       read compute write
                // 5: queue-N:   read compute    write read compute write
                // depending on pipeline type flag
                if (kernelNames.Length == 1)
                {

                    // for each device, run
                    // no need inter-device synchronization since execution is pipelinable
                    Parallel.For(0, workers.Length, i =>
                    {
                        computePipelined(
                            i, syncKernelName, kernelNames[0], arrs, numRepeats,
                            selectedGlobalRanges, readWrite, pipelineBlobs, selectedGlobalReferences,
                            pipelineType, computeId, elementsPerWorkItem);
                    });
                }
                else if (kernelNames.Length == 2)
                {
                    // if only 2 kernel names are executed
                    Parallel.For(0, workers.Length, i =>
                    {
                        // pipelined reads(from arrays) and computes for kernel1
                        computePipelined(
                            i, syncKernelName, kernelNames[0], arrs, numRepeats,
                            selectedGlobalRanges, readWrite, pipelineBlobs, selectedGlobalReferences,
                            pipelineType, computeId, elementsPerWorkItem,
                            ONLY_READ);

                        // pipelined computes and writes(to arrays) for kernel2 
                        computePipelined(
                            i, syncKernelName, kernelNames[1], arrs, numRepeats,
                            selectedGlobalRanges, readWrite, pipelineBlobs, selectedGlobalReferences,
                            pipelineType, computeId, elementsPerWorkItem,
                            ONLY_WRITE);
                    });
                }
                else if (kernelNames.Length > 2)
                {
                    // if there are N types of kernel executions
                    Parallel.For(0, workers.Length, i =>
                    {
                        // read all pipelined, execute first kernel pipelined

                        computePipelined(
                            i, syncKernelName, kernelNames[0], arrs, numRepeats,
                            selectedGlobalRanges, readWrite, pipelineBlobs, selectedGlobalReferences,
                            pipelineType, computeId, elementsPerWorkItem,
                            ONLY_READ);

                        // repeat intermediate kernels for n times
                        if (numRepeats > 0)
                            for (int j0 = 0; j0 < numRepeats; j0++)
                            {
                                for (int str = 1; str < kernelNames.Length - 1; str++)
                                    workers[i].compute(kernelNames[str], selectedGlobalReferences[i], selectedGlobalRanges[i], this.localRange, computeId);

                                if (syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1)
                                    workers[i].compute(syncKernelName, 0, this.localRange, this.localRange, -1);
                            }
                        else
                            for (int str = 1; str < kernelNames.Length - 1; str++)
                                workers[i].compute(kernelNames[str], selectedGlobalReferences[i], selectedGlobalRanges[i], this.localRange, computeId);



                        // execute last kernel pipelined, write to target arrays pipelined
                        computePipelined(
                            i, syncKernelName, kernelNames[1], arrs, numRepeats,
                            selectedGlobalRanges, readWrite, pipelineBlobs, selectedGlobalReferences,
                            pipelineType, computeId, elementsPerWorkItem,
                            ONLY_WRITE);
                    });
                }

                //pipeline is off
            }
            else
            {
                // no stream: yes stream
                // no pipeline, simple kernel execution as read -> compute -> write 

                // for each device, read all data
                if (workers.Length > 1)
                {
                    Parallel.For(0, workers.Length, i =>
                    {
                        if (selectedGlobalRanges[i] > 0)
                        {
                            lock (workers[i])
                            {
                                if (!noComputeMode)
                                {
                                    // set argument is no an enqueue so need to be taken care of first 
                                    // each worker is with different context, different device so being in parallel.for is not a problem
                                    for (int str = 0; str < kernelNames.Length; str++)
                                        workers[i].kernelArgument(kernelNames[str], arrs, elementsPerWorkItem, readWrite,computeId);

                                    if (syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1 /*1di 0 yapıldı*/)
                                        workers[i].kernelArgument(syncKernelName, arrs, elementsPerWorkItem, readWrite, computeId);
                                }
                                workers[i].startBench();

                                workers[i].writeToBuffer(arrs, selectedGlobalReferences[i], selectedGlobalRanges[i], computeId, readWrite, elementsPerWorkItem, enqueueMode);
                            }
                        }
                    });

                    // sync
                    // now all devices have up-to-date data
                    // run all devices(exeute kernel)  
                    if (!noComputeMode)
                    {
                        Parallel.For(0, workers.Length, i =>
                        {
                            if (selectedGlobalRanges[i] > 0)
                            {
                                lock (workers[i])
                                {
                                // to do: move repeats to C++ side to reduce interop overhead
                                if (numRepeats > 0)
                                    {

                                        if (kernelNames.Length > 1)
                                        {
                                            for (int j0 = 0; j0 < numRepeats; j0++)
                                            {
                                                for (int str = 0; str < kernelNames.Length; str++)
                                                    workers[i].compute(kernelNames[str], selectedGlobalReferences[i], selectedGlobalRanges[i], this.localRange, computeId, enqueueMode);

                                                if (syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1)
                                                    workers[i].compute(syncKernelName, 0, this.localRange, this.localRange, -1, enqueueMode);
                                            }
                                        }
                                        else
                                        {
                                            if (!(syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1))
                                            {
                                                workers[i].computeRepeated(
                                                    kernelNames[0], selectedGlobalReferences[i],
                                                    selectedGlobalRanges[i], this.localRange, computeId, numRepeats, enqueueMode);
                                            }
                                            else
                                            {
                                                workers[i].computeRepeatedWithSyncKernel(
                                                    kernelNames[0], selectedGlobalReferences[i],
                                                    selectedGlobalRanges[i], this.localRange, computeId, numRepeats, syncKernelName, enqueueMode);
                                            }
                                        }
                                    }
                                    else
                                        for (int str = 0; str < kernelNames.Length; str++)
                                            workers[i].compute(kernelNames[str], selectedGlobalReferences[i], selectedGlobalRanges[i], this.localRange, computeId, enqueueMode);
                                }
                            }
                        });
                    }


                    // sync

                    // write all device results to arrays
                    Parallel.For(0, workers.Length, i =>
                    {
                        lock (workers[i])
                        {
                            if (selectedGlobalRanges[i] > 0)
                            {
                                workers[i].readFromBuffer(arrs, selectedGlobalReferences[i], selectedGlobalRanges[i], computeId, readWrite, elementsPerWorkItem,i,workers.Length, enqueueMode);
                                workers[i].endBench(computeId);
                            }
                        }
                    });
                }
                else
                {
                    // no extra latency from parallel.for since there is only single device

                    if (selectedGlobalRanges[0] > 0)
                    {
                        lock (workers[0])
                        {
                            if (!noComputeMode)
                            {
                                // set argument is no an enqueue so need to be taken care of first 
                                for (int str = 0; str < kernelNames.Length; str++)
                                {
                                    workers[0].kernelArgument(kernelNames[str], arrs, elementsPerWorkItem, readWrite, computeId);
                                }

                                if (syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1 /*1di 0 yapıldı*/)
                                    workers[0].kernelArgument(syncKernelName, arrs, elementsPerWorkItem, readWrite, computeId);
                            }
                            if (!enqueueMode)
                                workers[0].startBench();

                            workers[0].writeToBuffer(arrs, selectedGlobalReferences[0], selectedGlobalRanges[0], computeId, readWrite, elementsPerWorkItem, enqueueMode, (enqueueMode && enqueueModeAsyncEnable) ? workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex) : null);
                        }
                    }

                    // sync
                    // now all devices have up-to-date data
                    // run all devices(exeute kernel)  
                    if (!noComputeMode)
                    {
                        if (selectedGlobalRanges[0] > 0)
                        {
                            lock (workers[0])
                            {

                                // to do: move repeats to C++ side to reduce interop overhead
                                if (numRepeats > 0)
                                {

                                    if (kernelNames.Length > 1)
                                    {
                                        for (int j0 = 0; j0 < numRepeats; j0++)
                                        {
                                            for (int str = 0; str < kernelNames.Length; str++)
                                                workers[0].compute(kernelNames[str], selectedGlobalReferences[0], selectedGlobalRanges[0], this.localRange, computeId, enqueueMode, (enqueueMode && enqueueModeAsyncEnable) ? workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex) : null);

                                            if (syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1)
                                                workers[0].compute(syncKernelName, 0, this.localRange, this.localRange, -1, enqueueMode, (enqueueMode && enqueueModeAsyncEnable) ? workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex) : null);
                                        }
                                    }
                                    else
                                    {
                                        if (!(syncKernelName != null && !syncKernelName.Equals("") && numRepeats > 1))
                                        {
                                            workers[0].computeRepeated(
                                                kernelNames[0], selectedGlobalReferences[0],
                                                selectedGlobalRanges[0], this.localRange, computeId, numRepeats, enqueueMode, (enqueueMode && enqueueModeAsyncEnable) ? workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex) : null);
                                        }
                                        else
                                        {
                                            workers[0].computeRepeatedWithSyncKernel(
                                                kernelNames[0], selectedGlobalReferences[0],
                                                selectedGlobalRanges[0], this.localRange, computeId, numRepeats, syncKernelName, enqueueMode, (enqueueMode && enqueueModeAsyncEnable) ? workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex) : null);
                                        }
                                    }
                                }
                                else
                                {
                                    for (int str = 0; str < kernelNames.Length; str++)
                                    {

                                        workers[0].compute(kernelNames[str], selectedGlobalReferences[0], selectedGlobalRanges[0], this.localRange, computeId, enqueueMode, (enqueueMode && enqueueModeAsyncEnable) ? workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex) : null);
                                    }
                                }
                            }
                        }
                    }

                    // sync
                   
                    
                    // write all device results to arrays
                    lock (workers[0])
                    {
                        if (selectedGlobalRanges[0] > 0)
                        {

                            workers[0].readFromBuffer(arrs, selectedGlobalReferences[0], selectedGlobalRanges[0], computeId, readWrite, elementsPerWorkItem,0,1, enqueueMode, (enqueueMode && enqueueModeAsyncEnable)?workers[0].nextComputeQueue(enqueueModeAsyncQueueIndex):null);

                            if (!enqueueMode)
                                workers[0].endBench(computeId);

                            if (enqueueMode)
                            {
                                workers[0].numComputeQueueUsed[0]++;

                                if (fineGrainedQueueControl)
                                {
                                    if(enqueueModeAsyncEnable && (workers[0].lastUsedCQ != null))
                                    {
                                        workers[0].lastUsedCQ.addMarkerForCounting();
                                    }
                                    else if (enqueueModeAsyncEnable)
                                    {
                                        workers[0].commandQueue.addMarkerForCounting();
                                    }

                                }
                            }
                        }
                    }

                }
            }


            foreach (var item in pinnedArrays)
            {
                if (!item.Equals(null))
                {

                    unpinArray(item);
                }
            }
            lastUsedComputeId = computeId;

        }

        internal int countMarkers()
        {

            int result = 0;
            for (int i = 0; i < workers.Length; i++)
            {
                result += workers[i].countMarkers();
            }
            return result;
        }

        internal int countMarkerCallbacks()
        {

            int result = 0;
            for(int i=0;i<workers.Length;i++)
            {
                result += workers[i].countMarkerCallbacks();
            }
            return result;
        }

        internal int lastUsedComputeId = 0;

        /// <summary>
        /// outputs to console: execution times per device and devices' target memory types(dedicated or RAM)
        /// </summary>
        /// <param name="computeId">compute id of compute action to be profiled. a compute action may behave differently than others because of data difference, kernel difference, read-write difference</param>
        /// <returns></returns>
        public string performanceReport(int computeId = 0)
        {
            // to do: mscorlib argument out of range exception
            // 'System.ArgumentOutOfRangeException' in mscorlib.dll
            StringBuilder sb = new StringBuilder();
            if (computeId == 0)
            {
                if (computeIdBenchmarks == null)
                {
                    sb.Append("Needs one more compute to profile. Load balancer needs multiple iterations to be useful.");
                    Console.WriteLine(sb.ToString());
                    return sb.ToString();
                }

                if((computeIdBenchmarks.Keys!=null) && (computeIdBenchmarks.Keys.Count>0))
                    computeId = computeIdBenchmarks.Keys.ElementAt(0);
                else
                {
                    sb.Append("Compute benchmark data is not ready. Load balancer needs multiple iterations to be useful.");
                    Console.WriteLine(sb.ToString());
                    return sb.ToString();
                }
            }

            if(globalRanges==null)
            {
                sb.Append("Error: Global range array is not ready.");
                Console.WriteLine(sb.ToString());
                return sb.ToString();
            }
            StringBuilder sbPercent = new StringBuilder("----- Load Distributions: ");
            int totalGlobalRange = 0;
            for (int i=0;i<workers.Length;i++)
            {
                totalGlobalRange += globalRanges[computeId][i];
            }

            for (int i = 0; i < workers.Length; i++)
            {
                sbPercent.AppendFormat(CultureInfo.InvariantCulture," [{0:###.0}%] -", 100.0f*((float)globalRanges[computeId][i] / (float)totalGlobalRange));
            }

            int count = 50;
            count -= sbPercent.ToString().Length;
            if (count < 0)
                count = 0;
            sbPercent.Append(new string('-', count));

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Compute-ID: " + computeId+"  "+sbPercent.ToString()+"------------------------------------------------"); 
            for (int i = 0; i < workers.Length; i++)
            {
                string strAdd = "Device " + i + "(" + (workers[i].gddr() ? "gddr" : "stream") + "): " + workers[i].deviceName.Trim();
                int countAdd = 50 - strAdd.Length;
                if (countAdd < 0)
                {
                    strAdd = strAdd.Remove(strAdd.Length + countAdd);
                    countAdd = 0;
                }
                string spaces = new string(' ', countAdd);
                sb.Append( strAdd+spaces+ " ||| time: " + String.Format(CultureInfo.InvariantCulture,"{0:###,###.##}", benchmarks(computeId)[i]) + "ms, workitems: " +String.Format(CultureInfo.InvariantCulture,"{0:#,###,###,###}", globalRanges[computeId][i]));
                sb.AppendLine();
            }

            sb.Append("-----------------------------------------------------------------------------------------------------------------");
            sb.AppendLine();
            Console.WriteLine(sb.ToString());
            return sb.ToString();
        }

        private int performanceHistoryDepth = 10;
        private Dictionary<int, double[][]> computeIdPerformanceHistory;
        private Dictionary<int, double[]> computeIdBenchmarks;


        /// <summary>
        /// get performance old values for smoothing load balancer, making OS peaks less effective
        /// </summary>
        /// <param name="computeId"></param>
        /// <returns></returns>
        public double[][] performanceHistory(int computeId)
        {
            if (computeIdPerformanceHistory == null)
                computeIdPerformanceHistory = new Dictionary<int, double[][]>();

            if (computeIdPerformanceHistory.ContainsKey(computeId))
            {

            }
            else
            {
                double[][] tmpHistory = new double[performanceHistoryDepth][];
                for(int i=0;i< performanceHistoryDepth; i++)
                {
                    tmpHistory[i] = new double[workers.Length];
                    for(int j=0;j< workers.Length;j++)
                    {
                        tmpHistory[i][j] = 0;
                    }
                }
                computeIdPerformanceHistory.Add(computeId,tmpHistory );
            }
            return computeIdPerformanceHistory[computeId];
        }


        /// <summary>
        /// execution timings of all devices for all compute id values
        /// </summary>
        /// <param name="computeId"></param>
        /// <returns></returns>
        public double[] benchmarks(int computeId)
        {
            if (computeIdBenchmarks == null)
                computeIdBenchmarks = new Dictionary<int, double[]>();
            
            if (computeIdBenchmarks.ContainsKey(computeId))
            {
                double[] result = computeIdBenchmarks[computeId];
                for (int i = 0; i < workers.Length; i++)
                {
                    result[i] = workers[i].benchmark[computeId];
                }
                return result;
            }
            else
            {
                double[] result = new double[workers.Length];
                for (int i = 0; i < workers.Length; i++)
                {
                    result[i] = workers[i].benchmark[computeId];
                }
                computeIdBenchmarks.Add(computeId, result);
                return result;
            }
        }

        /// <summary>
        /// returns device names such as 8-core desktop cpu or pitcairn or oland or Intel HD 400
        /// </summary>
        /// <returns></returns>
        public string[] deviceNames()
        {
            string[] str = new string[workers.Length];
            for (int i = 0; i < workers.Length; i++)
            {
                str[i] = workers[i].deviceName;
            }
            return str;
        }

        /// <summary>
        /// releases C++ resources
        /// </summary>
        ~Cores()
        {
            dispose();
        }

        /// <summary>
        /// release C++ resources
        /// </summary>
        public void dispose()
        {
            for (int i = 0; i < workers.Length; i++)
            {
                if (workers[i] != null)
                {
                    workers[i].dispose();
                    workers[i] = null;
                    Console.WriteLine("Workers dispose finished.");
                }
            }

            //if(platform!=null)
            //     platform.sil();
            
            for (int i = 0; i < platforms.Count; i++)
            {
                if (platforms[i] != null)
                    platforms[i].dispose();
                platforms[i] = null;
            }
            if (kernels != null)
                kernels.dispose();
            kernels = null;
            if (kernelNames != null)
            {
                int i_ = kernelNames.Length;
                for (int i = 0; i < i_; i++)
                {
                    if(kernelNames[i]!=null)
                        kernelNames[i].dispose();
                    kernelNames[i] = null;
                }
            }
        }




        // elementsPerWorkitem: array elements accessed per workitem (for all arrays)
        private void computePipelined(int i, string syncKernelName,
            string kernelName, object[] arrs,
            int numberOfKernelRepeats, int[] selectedGlobalRanges, string[] readWrite, int pipelineBlobs,
            int[] selectedGlobalOffsets, bool pipelineType, int computeId,
            int[] elementsPerWorkitem,  int read_write = -1 /* 0=read, 1=write, -1=read+write*/)
        {



            {
                if (selectedGlobalRanges[i] > 0)
                {

                    workers[i].kernelArgument(kernelName, arrs, elementsPerWorkitem, readWrite, computeId);
                    if (syncKernelName != null && !syncKernelName.Equals("") && numberOfKernelRepeats > 1)
                        workers[i].kernelArgument(syncKernelName, arrs, elementsPerWorkitem, readWrite, computeId);


                    if (read_write == ONLY_READ || read_write == -1)
                        workers[i].startBench();



                    // to do: writeAll version of this part will be written
                    // read all arrays that are to be read as a whole, not pipelined
                    if (read_write == ONLY_READ || read_write == -1)
                        workers[i].writeToBufferWithoutPartial(arrs, readWrite);

                    ClEventArray[] eArrRead = new ClEventArray[pipelineBlobs / 2 + 3];
                    ClEventArray[] eArrCompute = new ClEventArray[pipelineBlobs / 2 + 3];
                    ClEventArray[] eArrWrite = new ClEventArray[pipelineBlobs / 2 + 3];
                    for (int j = 0; j < pipelineBlobs / 2 + 3 /* read+compute+write = +2 */; j++)
                    {
                        eArrRead[j] = new ClEventArray();
                        eArrCompute[j] = new ClEventArray();
                        eArrWrite[j] = new ClEventArray();
                    }


                    // double the pipelines for a superscalar execution for event-driven pipeline
                    ClEventArray[] eArrRead2 = new ClEventArray[pipelineBlobs / 2 + 3];
                    ClEventArray[] eArrCompute2 = new ClEventArray[pipelineBlobs / 2 + 3];
                    ClEventArray[] eArrWrite2 = new ClEventArray[pipelineBlobs / 2 + 3];
                    for (int j = 0; j < pipelineBlobs / 2 + 3 /* read+compute+write = +2 */; j++)
                    {
                        eArrRead2[j] = new ClEventArray();
                        eArrCompute2[j] = new ClEventArray();
                        eArrWrite2[j] = new ClEventArray();
                    }



                    bool writeOperationIsDone = false;


                    if (pipelineType)
                    {
                        // EVENT driven pipeline

                        for (int j = 0; j < pipelineBlobs / 2 + 2 /* read+compute+write = +2 */; j++)
                        {


                            if (j <= pipelineBlobs / 2 - 1)
                            {

                                if (read_write == ONLY_READ || read_write == -1)
                                {
                                    ClEvent eWrite = new ClEvent();
                                    int isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                             selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * j,
                                             (selectedGlobalRanges[i] / pipelineBlobs),
                                             computeId,
                                             readWrite,
                                             elementsPerWorkitem,
                                             eArrRead[j], eWrite);
                                    if (isRead == 1)
                                    {
                                        eArrCompute[j + 1].add(eWrite);
                                        eArrWrite[j + 1].add(eWrite, true);
                                    }
                                }

                                if (read_write == ONLY_READ || read_write == -1)
                                {
                                    ClEvent eWrite = new ClEvent();
                                    int isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                             selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * j + selectedGlobalRanges[i] / 2,
                                             (selectedGlobalRanges[i] / pipelineBlobs),
                                             computeId,
                                             readWrite,
                                             elementsPerWorkitem,
                                             eArrRead2[j], eWrite, 1);
                                    if (isRead == 1)
                                    {
                                        eArrCompute2[j + 1].add(eWrite);
                                        eArrWrite2[j + 1].add(eWrite, true);
                                    }
                                }
                            }

                            if ((j >= 1) && (j <= pipelineBlobs / 2))
                            {

                                {
                                    ClEvent eWrite = new ClEvent();
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * (j - 1),
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       eArrCompute[j], eWrite);
                                    eArrRead[j + 1].add(eWrite);
                                    eArrWrite[j + 1].add(eWrite, true);
                                }

                                {
                                    ClEvent eWrite = new ClEvent();
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * (j - 1) + selectedGlobalRanges[i] / 2,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       eArrCompute2[j], eWrite, 1);
                                    eArrRead2[j + 1].add(eWrite);
                                    eArrWrite2[j + 1].add(eWrite, true);
                                }
                            }



                            if (j >= 2)
                            {

                                if (read_write == ONLY_WRITE || read_write == -1)
                                {
                                    ClEvent eWrite = new ClEvent();
                                    int isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * (j - 2) ,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       eArrWrite[j], eWrite);

                                    if (isWritten == 1)
                                    {
                                        writeOperationIsDone = true;
                                        eArrRead[j + 1].add(eWrite);
                                        eArrCompute[j + 1].add(eWrite, true);
                                    }
                                }

                                if (read_write == ONLY_WRITE || read_write == -1)
                                {
                                    ClEvent eWrite = new ClEvent();
                                    int isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * (j - 2)  + selectedGlobalRanges[i] / 2,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       eArrWrite2[j], eWrite, 1);

                                    if (isWritten == 1)
                                    {
                                        writeOperationIsDone = true;
                                        eArrRead2[j + 1].add(eWrite);
                                        eArrCompute2[j + 1].add(eWrite, true);
                                    }
                                }
                            }
                            // pipeline loop end
                        }
                    }
                    else
                    {

                        // DRIVER driven pipeline
                        // events here for future event-constrained pipeline version
                        if (pipelineBlobs % 4 != 0)
                        {
                            Console.WriteLine("Pipeline stages are not exactly multiple of 4!");
                            dispose();
                            return;
                        }




                        for (int k = 0; k < pipelineBlobs; k++)
                        {

                            if(read_write==ONLY_READ || read_write==-1)
                            {
                                // write start (C# - side "read" operation)
                                ClEvent empty = new ClEvent();
                                ClEventArray emptyArr = new ClEventArray();
                                int isRead = 0;
                                // hardcoded commandqueues, could be array instead 
                                if (k % 16 == 0)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue);
                                }
                                else if (k % 16 == 1)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue2);
                                }
                                else if (k % 16 == 2)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue3);
                                }
                                else if (k % 16 == 3)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue4);
                                }
                                else if (k % 16 == 4)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue5);
                                }
                                else if (k % 16 == 5)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue6);
                                }
                                else if (k % 16 == 6)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue7);
                                }
                                else if (k % 16 == 7)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue8);
                                }
                                else if (k % 16 == 8)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue9);
                                }
                                else if (k % 16 == 9)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue10);
                                }
                                else if (k % 16 == 10)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue11);
                                }
                                else if (k % 16 == 11)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue12);
                                }
                                else if (k % 16 == 12)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue13);
                                }
                                else if (k % 16 == 13)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue14);
                                }
                                else if (k % 16 == 14)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue15);
                                }
                                else if (k % 16 == 15)
                                {
                                    isRead = workers[i].writeToBufferUsingQueueReadPartialEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue16);
                                }

                                if (isRead != 0)
                                    empty.dispose();
                                emptyArr.dispose();
                                // write end (C# "read" end)
                            }


                            {
                                // compute start
                                ClEvent empty = new ClEvent();
                                ClEventArray emptyArr = new ClEventArray();
                                if (k % 16 == 0)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue);
                                }
                                else if (k % 16 == 1)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue2);
                                }
                                else if (k % 16 == 2)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue3);
                                }
                                else if (k % 16 == 3)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue4);
                                }
                                else if (k % 16 == 4)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue5);
                                }
                                else if (k % 16 == 5)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue6);
                                }
                                else if (k % 16 == 6)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue7);
                                }
                                else if (k % 16 == 7)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue8);
                                }
                                else if (k % 16 == 8)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue9);
                                }
                                else if (k % 16 == 9)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue10);
                                }
                                else if (k % 16 == 10)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue11);
                                }
                                else if (k % 16 == 11)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue12);
                                }
                                else if (k % 16 == 12)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue13);
                                }
                                else if (k % 16 == 13)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue14);
                                }
                                else if (k % 16 == 14)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue15);
                                }
                                else if (k % 16 == 15)
                                {
                                    workers[i].computeQueueEvent(kernelName,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs), localRange, computeId,
                                       emptyArr, empty, 0, workers[i].commandQueue16);
                                }

                                empty.dispose();
                                emptyArr.dispose();
                                // compute end
                            }


                            if (read_write == ONLY_WRITE || read_write == -1)
                            {
                                // read begin - C# side "write" operation
                                ClEvent empty = new ClEvent();
                                ClEventArray emptyArr = new ClEventArray();
                                int isWritten = 0;
                                if (k % 16 == 0)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue);
                                }
                                else if (k % 16 == 1)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue2);
                                }
                                else if (k % 16 == 2)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue3);
                                }
                                else if (k % 16 == 3)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k ,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue4);
                                }
                                else if (k % 16 == 4)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k ,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue5);
                                }
                                else if (k % 16 == 5)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue6);
                                }
                                else if (k % 16 == 6)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue7);
                                }
                                else if (k % 16 == 7)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue8);
                                }
                                else if (k % 16 == 8)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue9);
                                }
                                else if (k % 16 == 9)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue10);
                                }
                                else if (k % 16 == 10)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue11);
                                }
                                else if (k % 16 == 11)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue12);
                                }
                                else if (k % 16 == 12)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue13);
                                }
                                else if (k % 16 == 13)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue14);
                                }
                                else if (k % 16 == 14)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue15);
                                }
                                else if (k % 16 == 15)
                                {
                                    isWritten = workers[i].readFromBufferUsingQueueWriteEvent(arrs,
                                       selectedGlobalOffsets[i] + (selectedGlobalRanges[i] / pipelineBlobs) * k ,
                                       (selectedGlobalRanges[i] / pipelineBlobs),
                                       computeId,
                                       readWrite,
                                       elementsPerWorkitem,
                                       emptyArr, empty, 0, workers[i].commandQueue16);
                                }
                                empty.dispose();
                                emptyArr.dispose();
                                // write end
                            }

                        }


                    }

                    // todo: test here. only for single GPUs
                    // read all arrays that are to be read as a whole, not pipelined
                    if (read_write == ONLY_WRITE || read_write == -1)
                        workers[i].readFromBufferAllData(arrs, readWrite,i,workers.Length);

                    if (read_write == ONLY_WRITE || read_write == -1)
                    {
                        if (pipelineType)
                        {
                            // event driven pipelining sync
                            workers[i].bufferReadQueueWriteFlush();
                            workers[i].computeQueueFlush();
                            workers[i].writeToBufferUsingQueueReadFlush();
                            workers[i].readBufferQueueWriteFlush2();
                            workers[i].computeQueueFlush2();
                            workers[i].writeToBufferUsingQueueReadFlush2();



                            if (writeOperationIsDone)
                            {
                                workers[i].readBufferQueueWriteFinish();
                                workers[i].readBufferQueueWriteFinish2();
                            }
                            else
                            {
                                workers[i].computeQueueFinish();
                                workers[i].computeQueueFinish2();
                            }
                        }
                        else
                        {
                            // driver-driven pipelining sync


                            Parallel.For(0, 8, m =>
                            {
                                if (m == 0)
                                {
                                    workers[i].computeQueueFlush();
                                    workers[i].computeQueueFlush16();
                                    workers[i].computeQueueFinish();
                                    workers[i].computeQueueFinish16();
                                }
                                if (m == 1)
                                {
                                    workers[i].computeQueueFlush2();
                                    workers[i].computeQueueFlush15();
                                    workers[i].computeQueueFinish2();
                                    workers[i].computeQueueFinish15();
                                }
                                if (m == 2)
                                {
                                    workers[i].computeQueueFlush3();
                                    workers[i].computeQueueFlush14();
                                    workers[i].computeQueueFinish3();
                                    workers[i].computeQueueFinish14();
                                }
                                if (m == 3)
                                {
                                    workers[i].computeQueueFlush4();
                                    workers[i].computeQueueFlush13();
                                    workers[i].computeQueueFinish4();
                                    workers[i].computeQueueFinish13();
                                }

                                if (m == 4)
                                {
                                    workers[i].computeQueueFlush5();
                                    workers[i].computeQueueFlush12();
                                    workers[i].computeQueueFinish5();
                                    workers[i].computeQueueFinish12();
                                }

                                if (m == 5)
                                {
                                    workers[i].computeQueueFlush6();
                                    workers[i].computeQueueFlush11();
                                    workers[i].computeQueueFinish6();
                                    workers[i].computeQueueFinish11();
                                }

                                if (m == 6)
                                {
                                    workers[i].computeQueueFlush7();
                                    workers[i].computeQueueFlush10();
                                    workers[i].computeQueueFinish7();
                                    workers[i].computeQueueFinish10();
                                }

                                if (m == 7)
                                {
                                    workers[i].computeQueueFlush8();
                                    workers[i].computeQueueFlush9();
                                    workers[i].computeQueueFinish8();
                                    workers[i].computeQueueFinish9();
                                }
                            });

                        }
                    }

                    // release all event resources from C++ "C" space
                    for (int j = 0; j < pipelineBlobs / 2 + 3 /* read+compute+write = +2 */; j++)
                    {
                        eArrRead[j].dispose();
                        eArrCompute[j].dispose();
                        eArrWrite[j].dispose();

                        eArrRead2[j].dispose();
                        eArrCompute2[j].dispose();
                        eArrWrite2[j].dispose();
                    }


                    // finish benchmarking
                    if(read_write==ONLY_WRITE || read_write==-1)
                        workers[i].endBench(computeId);
                }
            }
        }
    }
}
