﻿using RLM.Models.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RLM.Models;
using System.Collections.Concurrent;
using RLM.Database;
using System.Threading;
using RLM.Database.Utility;

namespace RLM.Memory
{
    public class Manager : IManager
    {
        private const int MAX_ALLOC = 100000; //1000000;

        private BlockingCollection<Rneuron> rneuron_queue;
        private BlockingCollection<Session> session_queue;
        private BlockingCollection<Session> savedSession_queue;
        private BlockingCollection<Solution> solution_queue;
        private BlockingCollection<Case> case_queue;
        private List<Session> sessions = new List<Session>();

        private RlmDbMgr rlmDb;
        private RlmObjectEnqueuer rlmDbEnqueuer;

        private CancellationTokenSource ctSourceSessions;
        private CancellationToken tokenSessions;

        private CancellationTokenSource ctSourceCases;
        private CancellationToken tokenCases;

        private bool trainingDone = false;
        private bool sessionsDone = false;
        private int totalSessionsCount = 0;
        private Task sessionCreateTask;
        private Task sessionUpdateTask;
        private Task caseTask;
        private int iConcurrencyLevel = Environment.ProcessorCount;
        private int rneuronsBoundedCapacity;
        private int sessionsBoundedCapacity;
        private int solutionsBoundedCapacity;
        private int casesBoundedCapacity;

        private System.Diagnostics.Stopwatch dbSavingTime = new System.Diagnostics.Stopwatch();
        private System.Timers.Timer progressUpdater = new System.Timers.Timer();
        private double lastProgress = -1;

        // temp for benchmarks
        public uint CacheBoxCount { get; set; } = 0;
        public List<TimeSpan> GetRneuronTimes { get; set; }
        public List<TimeSpan> RebuildCacheboxTimes { get; set; }
        public System.Diagnostics.Stopwatch SwGetRneuron { get; set; }
        public System.Diagnostics.Stopwatch SwRebuildCache { get; set; }
        // temp for benchmarks

        public event DataPersistenceCompleteDelegate DataPersistenceComplete;
        public event DataPersistenceProgressDelegate DataPersistenceProgress;
        
        public IRlmNetwork Network { get; private set; }
        
        public SortedList<RlmInputKey, RlmInputValue> DynamicInputs { get; set; }

        //public ConcurrentDictionary<long, SortedList<double, HashSet<long>>> DynamicLinearInputs { get; set; } = new ConcurrentDictionary<long, SortedList<double, HashSet<long>>>();
        //public ConcurrentDictionary<long, Dictionary<string, HashSet<long>>> DynamicDistinctInputs { get; set; } = new ConcurrentDictionary<long, Dictionary<string, HashSet<long>>>();

        public ConcurrentDictionary<long, HashSet<SolutionOutputSet>> DynamicOutputs { get; set; } = new ConcurrentDictionary<long, HashSet<SolutionOutputSet>>();
        public ConcurrentDictionary<long, Rneuron> Rneurons { get; set; } = new ConcurrentDictionary<long, Rneuron>();
        public ConcurrentQueue<Rneuron> Rneurons2 { get; set; } = new ConcurrentQueue<Rneuron>();

        public ConcurrentDictionary<long, Session> Sessions { get; set; } = new ConcurrentDictionary<long, Session>();
        public ConcurrentQueue<Session> Sessions2 { get; set; } = new ConcurrentQueue<Session>();
        public ConcurrentQueue<Session> Sessions3 { get; set; } = new ConcurrentQueue<Session>();

        public ConcurrentDictionary<long, Solution> Solutions { get; set; } = new ConcurrentDictionary<long, Solution>();
        public ConcurrentQueue<Solution> Solutions2 { get; set; } = new ConcurrentQueue<Solution>();

        public ConcurrentDictionary<long, Dictionary<long, BestSolution>> BestSolutions { get; set; } = new ConcurrentDictionary<long, Dictionary<long, BestSolution>>();

        public HashSet<BestSolution> BestSolutionStaging { get; set; } = new HashSet<BestSolution>();

        public ConcurrentBag<Case> Cases { get; set; } = new ConcurrentBag<Case>();
        public ConcurrentQueue<Case> Cases2 { get; set; } = new ConcurrentQueue<Case>();

        public BestSolutionCacheBox CacheBox { get; set; } = new BestSolutionCacheBox();

        public double MomentumAdjustment { get; set; } = 25;
        public double CacheBoxMargin { get; set; } = 0;
        public bool UseMomentumAvgValue { get; set; } = false;

        private readonly ConcurrentQueue<Queue<Case>> MASTER_CASE_QUEUE = new ConcurrentQueue<Queue<Case>>();
        private Queue<Case> caseQueue = null;
        private object caseQueue_lock = new object();

        /// <summary>
        /// Initializes memory manager
        /// </summary>
        /// <param name="databaseName">datbase name</param>
        public Manager(IRlmNetwork network, bool trackStats = false)
        {
            Network = network;
            rneuron_queue = new BlockingCollection<Rneuron>();
            session_queue = new BlockingCollection<Session>();
            savedSession_queue = new BlockingCollection<Session>();
            solution_queue = new BlockingCollection<Solution>();
            case_queue = new BlockingCollection<Case>();

            rlmDb = new RlmDbMgr(network.DatabaseName, network.PersistData);
            rlmDbEnqueuer = new RlmObjectEnqueuer();

            ctSourceSessions = new CancellationTokenSource();
            tokenSessions = ctSourceSessions.Token;

            ctSourceCases = new CancellationTokenSource();
            tokenCases = ctSourceCases.Token;

            progressUpdater.Interval = 1000;
            progressUpdater.Elapsed += ProgressUpdater_Elapsed;

            if (trackStats)
            {
                GetRneuronTimes = new List<TimeSpan>();
                RebuildCacheboxTimes = new List<TimeSpan>();
                SwGetRneuron = new System.Diagnostics.Stopwatch();
                SwRebuildCache = new System.Diagnostics.Stopwatch();
            }

            MASTER_CASE_QUEUE.Enqueue(caseQueue = new Queue<Case>());
        }
        
        /// <summary>
        /// Save created network
        /// </summary>
        /// <param name="rnetwork">current rnetwork</param>
        /// <param name="io_type">type of input and output</param>
        /// <param name="inputs">List of inputs</param>
        /// <param name="outputs">List of outputs</param>
        public void NewNetwork(Rnetwork rnetwork, Input_Output_Type io_type, List<Input> inputs, List<Output> outputs)
        {
            //todo: rnn dbmanager and save
            rlmDb.SaveNetwork(rnetwork, io_type, inputs, outputs);
            double r = 1.0;
            double s = 1.0;

            foreach(var input in inputs)
            {
                r *= input.Max;
            }

            rneuronsBoundedCapacity = Convert.ToInt32(r);

            foreach(var output in outputs)
            {
                s *= output.Max;
            }

            solutionsBoundedCapacity = Convert.ToInt32(s);

            //Rneurons = new ConcurrentDictionary<long, Rneuron>(iConcurrencyLevel, rneuronsBoundedCapacity);
            //Solutions = new ConcurrentDictionary<long, Solution>(iConcurrencyLevel, solutionsBoundedCapacity);

        }
        /// <summary>
        /// Save the new network and send it to a task. It also starts the database workers
        /// </summary>
        /// <param name="rnetwork"></param>
        /// <param name="io_types"></param>
        /// <param name="inputs"></param>
        /// <param name="outputs"></param>
        /// <param name="rnn_net"></param>
        public void NewNetwork(Rnetwork rnetwork, List<Input_Output_Type> io_types, List<Input> inputs, List<Output> outputs, IRlmNetwork rnn_net)
        {
            //todo: rnn dbmanager and save
            dbSavingTime.Start();

            RlmDbLogger.Info("\n" + string.Format("[{0:G}]: Started saving data for {1}...", DateTime.Now, Network.DatabaseName), Network.DatabaseName);

            Task t1 = Task.Run(() =>
            {
                rlmDb.SaveNetwork(rnetwork, io_types, inputs, outputs, rnn_net);
            });

            t1.Wait();
            StartRlmDbWorkers();

            //InitStorage(inputs, outputs);

            //rlmDb.SaveNetwork(rnetwork, io_types, inputs, outputs, rnn_net);
        }

        public void InitStorage(List<Input> inputs, List<Output> outputs)
        {
            //double r = 1.0;
            //double s = 1.0;

            //foreach (var input in inputs)
            //{
            //    r *= input.Max;
            //}

            //rneuronsBoundedCapacity = Convert.ToInt32(r);

            //foreach (var output in outputs)
            //{
            //    s *= output.Max;
            //}

            //solutionsBoundedCapacity = Convert.ToInt32(s);

            //Rneurons = new ConcurrentDictionary<long, Rneuron>(iConcurrencyLevel, rneuronsBoundedCapacity);
            //Solutions = new ConcurrentDictionary<long, Solution>(iConcurrencyLevel, solutionsBoundedCapacity);

            //Rneurons = new ConcurrentDictionary<long, Rneuron>();
            //Solutions = new ConcurrentDictionary<long, Solution>();
        }
        /// <summary>
        /// Add the session to queue
        /// </summary>
        /// <param name="key">session Id</param>
        /// <param name="session">current session</param>
        /// <returns></returns>
        public bool AddSessionToQueue(long key, Session session)
        {
            bool retVal = false;

            //retVal = Sessions.TryAdd(key, session);
            //if (retVal)
            //{
            //    session_queue.Add(session);
            //}

            Sessions2.Enqueue(session);

            return retVal;
        }
        /// <summary>
        /// Add session to be updated to queue
        /// </summary>
        /// <param name="session">current session</param>
        /// <returns></returns>
        public bool AddSessionUpdateToQueue(Session session)
        {
            bool retVal = false;

            //retVal = savedSession_queue.TryAdd(session);

            //if(retVal)
            //{

            //}

            Sessions3.Enqueue(session);

            return retVal;
        }
        /// <summary>
        /// Add case to queue
        /// </summary>
        /// <param name="key">cycle Id</param>
        /// <param name="c_case">current case</param>
        public void AddCaseToQueue(long key, Case c_case)
        {
            //Cases.Add(c_case);
            //Cases2.Enqueue(c_case);

            if (caseQueue.Count() >= MAX_ALLOC)
            {
                MASTER_CASE_QUEUE.Enqueue(caseQueue = new Queue<Case>(MAX_ALLOC));
            }

            lock (caseQueue_lock)
            {
                caseQueue.Enqueue(c_case);
            }
        }
        /// <summary>
        /// Gets existing Rneuron and creates a new one if not existing
        /// </summary>
        /// <param name="inputs">Inputs with value</param>
        /// <param name="rnetworkID">Current NetworkId</param>
        /// <returns></returns>
        public GetRneuronResult GetRneuronFromInputs(IEnumerable<RlmIOWithValue> inputs, long rnetworkID)
        {
            GetRneuronResult retVal = new GetRneuronResult();
            Rneuron rneuron = null;

            // generate key based on input values
            long rneuronId = Util.GenerateHashKey(inputs.Select(a => a.Value).ToArray());

            // create new rneuron if not exists
            if (!Rneurons.TryGetValue(rneuronId, out rneuron))
            {
                rneuron = new Rneuron() { ID = rneuronId, Rnetwork_ID = rnetworkID };

                bool isFirstInput = true;
                RlmInputValue lastInputValue = null;
                int cnt = 0;

                IComparer<RlmInputKey> distinctComparer = new RlmInputKeyDistinctComparer();
                IComparer<RlmInputKey> linearComparer = new RlmInputKeyLinearComparer();

                foreach(var i in inputs)
                {
                    // create IVR instance
                    var ivr = new Input_Values_Rneuron()
                    {
                        ID = Util.GenerateHashKey(rneuronId, i.ID),
                        Value = i.Value,
                        Input_ID = i.ID,
                        Rneuron_ID = rneuronId,
                        DotNetType = i.DotNetType,
                        InputType = i.Type
                    };
                    rneuron.Input_Values_Reneurons.Add(ivr);

                    RlmInputKey inputKey = new RlmInputKey() { Value = ivr.Value, InputNum = cnt, Type = i.Type };
                    inputKey.DoubleValue = (i.Type == Enums.RlmInputType.Linear) ? Convert.ToDouble(ivr.Value) : 0D;
                    RlmInputValue inputVal = null;

                    if (!isFirstInput)
                    {
                        if (lastInputValue.RelatedInputs == null)
                            lastInputValue.RelatedInputs = new SortedList<RlmInputKey, RlmInputValue>(i.Type == Enums.RlmInputType.Linear ? linearComparer : distinctComparer);

                        if (!lastInputValue.RelatedInputs.TryGetValue(inputKey, out inputVal))
                        {
                            inputVal = new RlmInputValue();
                            lastInputValue.RelatedInputs.Add(inputKey, inputVal);
                        }

                        lastInputValue = inputVal;
                    }
                    else
                    {
                        if (DynamicInputs == null)
                            DynamicInputs = new SortedList<RlmInputKey, RlmInputValue>(i.Type == Enums.RlmInputType.Linear ? linearComparer : distinctComparer);

                        isFirstInput = false;
                        if (!DynamicInputs.TryGetValue(inputKey, out inputVal))
                        {
                            inputVal = new RlmInputValue();
                            DynamicInputs.Add(inputKey, inputVal);
                        }

                        lastInputValue = inputVal;
                    }
                    cnt++;                   
                }

                lastInputValue.RneuronId = rneuronId;

                Rneurons.TryAdd(rneuronId, rneuron);
                //rneuron_queue.Add(retVal);
                //if (Rneurons.TryAdd(rneuronId, retVal))
                //{
                //}
                //Rneurons2.Enqueue(retVal);
                //rneuron_queue.Add(retVal);

                retVal.Rneuron = rneuron;
                retVal.ExistsInCache = false;
            }
            else
            {
                retVal.Rneuron = rneuron;
                retVal.ExistsInCache = true;
            }

            return retVal;
        }

        object lockDynamicInputs = new object();
        /// <summary>
        /// Sets the Rneuron
        /// </summary>
        /// <param name="rneuron"></param>
        public void SetRneuronWithInputs(Rneuron rneuron)
        {
            var rneuronId = rneuron.ID;

            // add rneuron to cache
            Rneurons.TryAdd(rneuronId, rneuron);

            bool isFirstInput = true;
            RlmInputValue lastInputValue = null;
            int cnt = 0;

            IComparer<RlmInputKey> distinctComparer = new RlmInputKeyDistinctComparer();
            IComparer<RlmInputKey> linearComparer = new RlmInputKeyLinearComparer();

            // build dynamic inputs
            foreach (var i in rneuron.Input_Values_Reneurons)
            {
                RlmInputKey inputKey = new RlmInputKey() { Value = i.Value, InputNum = cnt, Type = i.InputType };
                inputKey.DoubleValue = (i.InputType == Enums.RlmInputType.Linear) ? Convert.ToDouble(i.Value) : 0D;
                RlmInputValue inputVal = null;

                if (!isFirstInput)
                {
                    if (lastInputValue.RelatedInputs == null)
                        lastInputValue.RelatedInputs = new SortedList<RlmInputKey, RlmInputValue>(i.InputType == Enums.RlmInputType.Linear ? linearComparer : distinctComparer);

                    lock (lastInputValue.RelatedInputs)
                    {
                        if (!lastInputValue.RelatedInputs.TryGetValue(inputKey, out inputVal))
                        {
                            inputVal = new RlmInputValue();
                            lastInputValue.RelatedInputs.Add(inputKey, inputVal);
                        }
                    }

                    lastInputValue = inputVal;
                }
                else
                {
                    if (DynamicInputs == null)
                        DynamicInputs = new SortedList<RlmInputKey, RlmInputValue>(i.InputType == Enums.RlmInputType.Linear ? linearComparer : distinctComparer);

                    isFirstInput = false;
                    lock (lockDynamicInputs)
                    {
                        if (!DynamicInputs.TryGetValue(inputKey, out inputVal))
                        {
                            inputVal = new RlmInputValue();
                            DynamicInputs.Add(inputKey, inputVal);
                        }
                    }
                    lastInputValue = inputVal;
                }
                cnt++;
            }

            lastInputValue.RneuronId = rneuronId;
        }
        /// <summary>
        /// Gets best Solution
        /// </summary>
        /// <param name="inputs"></param>
        /// <param name="linearTolerance"></param>
        /// <param name="predict"></param>
        /// <returns></returns>
        public Solution GetBestSolution(IEnumerable<RlmIOWithValue> inputs, double trainingLinearTolerance = 0, bool predict = false, double predictLinearTolerance = 0)
        {
            bool useLinearTolerance = ((predict && predictLinearTolerance > 0) || !predict) ? true : false;
            double linearTolerance = (predict) ? predictLinearTolerance : trainingLinearTolerance;

            Solution retVal = null;
            
            var comparer = new DynamicInputComparer();//Util.DynamicInputComparer;
            List<long> rneuronsFound = new List<long>();
            
            var rangeInfos = new Dictionary<int, InputRangeInfo>();
            int cnt = 0;
            foreach (var item in inputs)
            {
                if (item.Type == Enums.RlmInputType.Linear)
                {
                    double val = Convert.ToDouble(item.Value);
                    double off = (item.Max - item.Min) * ((linearTolerance == 0) ? 0 : (linearTolerance / 100D));

                    rangeInfos.Add(cnt, new InputRangeInfo() { InputId = item.ID, InputType = item.Type, FromValue = val - off, ToValue = val + off });
                }
                else
                {
                    rangeInfos.Add(cnt, new InputRangeInfo() { InputId = item.ID, InputType = item.Type, Value = item.Value });
                }
                cnt++;
            }

            //TODO Cache Box, current implementation is slow don't know why
            if (useLinearTolerance)
            {
                if (CacheBox.IsWithinRange(rangeInfos, linearTolerance))
                {
                    SwGetRneuron?.Start();

                    RlmInputValue.RecurseInputForMatchingRneurons(CacheBox.CachedInputs, rangeInfos, rneuronsFound);

                    SwGetRneuron?.Stop();
                    GetRneuronTimes?.Add(SwGetRneuron.Elapsed);
                    SwGetRneuron?.Reset();
                }
                else
                {
                    SwRebuildCache?.Start();

                    CacheBoxCount++;
                    CacheBox.Clear();

                    var cacheBoxRangeInfos = new Dictionary<int, InputRangeInfo>();
                    int cacheRangeCnt = 0;
                    foreach (var item in inputs)
                    {
                        if (item.Type == Enums.RlmInputType.Linear)
                        {
                            double val = Convert.ToDouble(item.Value);
                            double dataOff = (item.Max - item.Min) * ((linearTolerance == 0) ? 0 : (linearTolerance / 100D));
                            //double cacheMargin = (CacheBoxMargin == 0) ? 0 : ((item.Max - item.Min) * (CacheBoxMargin / 100));
                            double momentum = item.InputMomentum.MomentumDirection;
                            double toOff = 0;
                            double fromOff = 0;
                            double cacheOff = 0;

                            if (UseMomentumAvgValue)
                                cacheOff = item.InputMomentum.MomentumValue * MomentumAdjustment;
                            else
                                cacheOff = (item.Max - item.Min) * ((linearTolerance == 0) ? 0 : (MomentumAdjustment / 100D));
                            

                            if (momentum > 0)
                            {
                                var offset = momentum * cacheOff;
                                toOff = val + dataOff + (cacheOff + offset);
                                fromOff = val - dataOff - (cacheOff - offset);
                            }
                            else if (momentum < 0)
                            {
                                var offset = Math.Abs(momentum) * cacheOff;
                                toOff = val + dataOff + (cacheOff - offset);
                                fromOff = val - dataOff - (cacheOff + offset);
                            }
                            else
                            {
                                toOff = val + dataOff + cacheOff;
                                fromOff = val - dataOff - cacheOff;
                            }

                            double cacheMargin = (CacheBoxMargin == 0) ? 0 : (cacheOff) * (CacheBoxMargin / 100D);

                            toOff += cacheMargin;
                            fromOff -= cacheMargin;

                            cacheBoxRangeInfos.Add(cacheRangeCnt, new InputRangeInfo() { InputId = item.ID, FromValue = Math.Ceiling(fromOff), ToValue = Math.Ceiling(toOff) });
                        }
                        else
                        {
                            cacheBoxRangeInfos.Add(cacheRangeCnt, new InputRangeInfo() { InputId = item.ID, Value = item.Value });
                        }
                        cacheRangeCnt++;
                    }

                    CacheBox.SetRanges(cacheBoxRangeInfos.Values);

                    CacheBox.CachedInputs = RlmInputValue.RecurseInputForMatchingRneuronsForCaching(DynamicInputs, cacheBoxRangeInfos, rangeInfos, rneuronsFound);
                    //RnnInputValue.RecurseInputForMatchingRneurons(CacheBox.CachedInputs, rangeInfos, rneuronsFound);

                    SwRebuildCache?.Stop();
                    RebuildCacheboxTimes?.Add(SwRebuildCache.Elapsed);
                    SwRebuildCache?.Reset();
                }
            }
            else
            {
                RlmInputValue.RecurseInputForMatchingRneurons(DynamicInputs, rangeInfos, rneuronsFound);
            }

            BestSolution currBS = null;
            foreach (var rneuronId in rneuronsFound)
            {
                Dictionary<long, BestSolution> bsDict;
                if (BestSolutions.TryGetValue(rneuronId, out bsDict))
                {
                    foreach (var bs in bsDict.Values)
                    {
                        if (!predict)
                        {
                            if (currBS != null)
                            {
                                if (bs.CycleScore > currBS.CycleScore)
                                {
                                    currBS = bs;
                                }
                                else if (bs.CycleScore == currBS.CycleScore && bs.SessionScore >= currBS.SessionScore && bs.CycleOrder >= currBS.CycleOrder)
                                {
                                    currBS = bs;
                                }
                            }
                            else
                            {
                                currBS = bs;
                            }
                        }
                        else
                        {
                            if (currBS != null)
                            {
                                if (bs.SessionScore > currBS.SessionScore)
                                {
                                    currBS = bs;
                                }
                                else if (bs.SessionScore == currBS.SessionScore && bs.CycleScore >= currBS.CycleScore && bs.CycleOrder >= currBS.CycleOrder)
                                {
                                    currBS = bs;
                                }
                            }
                            else
                            {
                                currBS = bs;
                            }
                        }
                    }
                }
            }
            
            if (currBS != null)
            {
                retVal = Solutions[currBS.SolutionId];
            }

            return retVal;
        }
        /// <summary>
        /// Sets best solution
        /// </summary>
        /// <param name="bestSolution"></param>
        public void SetBestSolution(BestSolution bestSolution)
        {
            Dictionary<long, BestSolution> innerBestSolutions;
            if (BestSolutions.TryGetValue(bestSolution.RneuronId, out innerBestSolutions))
            {
                innerBestSolutions.Add(bestSolution.SolutionId, bestSolution);
            }
            else
            {
                innerBestSolutions = new Dictionary<long, BestSolution>();
                innerBestSolutions.Add(bestSolution.SolutionId, bestSolution);
                BestSolutions.TryAdd(bestSolution.RneuronId, innerBestSolutions);
            }
        }
        /// <summary>
        /// Gets a random solution from outputs or randomize 
        /// </summary>
        /// <param name="randomnessCurrVal"></param>
        /// <param name="outputs"></param>
        /// <param name="bestSolutionId"></param>
        /// <returns></returns>
        public GetSolutionResult GetRandomSolutionFromOutput(double randomnessCurrVal, IEnumerable<RlmIO> outputs, long? bestSolutionId = null)
        {
            GetSolutionResult retVal = null;

            if (outputs.Count() == 1)
            {
                IEnumerable<RlmIOWithValue> outputWithValues = GetRandomOutputValues(outputs);
                retVal = GetSolutionFromOutputs(outputWithValues);
            }
            else
            {
                var outputsWithVal = new List<RlmIOWithValue>();

                // check if best solution was passed in as parameter or not
                if (bestSolutionId.HasValue)
                {
                    IEnumerable<Output_Values_Solution> bestOutputs = null;
                    int cntRandomValues = 0;

                    foreach (var item in outputs)
                    {
                        // use best solution value if randomness outside threshold
                        int randomnessValue = Util.Randomizer.Next(1, 101);
                        if (randomnessValue > randomnessCurrVal)
                        {
                            if (bestOutputs == null)
                            {
                                //bestOutputs = db.Output_Values_Solutions.Where(a => a.Solution_ID == bestSolutionId.Value);
                                Solution solution = Solutions[bestSolutionId.Value];
                                bestOutputs = solution.Output_Values_Solutions;
                            }

                            var bestOutput = bestOutputs.FirstOrDefault(a => a.Output_ID == item.ID);
                            outputsWithVal.Add(new RlmIOWithValue(item, bestOutput.Value));
                        }
                        else // get random value
                        {
                            string value = GetRandomValue(item);
                            outputsWithVal.Add(new RlmIOWithValue(item, value));
                            cntRandomValues++;
                        }
                    }

                    // if no random values were assigned then we randomly select one to randomize
                    // this is to ensure we have at least one random output value
                    if (cntRandomValues == 0)
                    {
                        var index = Util.Randomizer.Next(0, outputsWithVal.Count);
                        var output = outputsWithVal.ElementAt(index);
                        string value = GetRandomValue(output);
                        output.Value = value;
                    }
                }
                else // no best solution, so we give out all random values
                {
                    outputsWithVal.AddRange(GetRandomOutputValues(outputs));
                }

                retVal = GetSolutionFromOutputs(outputsWithVal);
            }

            return retVal;
        }
        /// <summary>
        /// Gets solution and record ideal score
        /// </summary>
        /// <param name="outputs"></param>
        /// <returns></returns>
        public GetSolutionResult GetSolutionFromOutputs(IEnumerable<RlmIOWithValue> outputs)
        {
            GetSolutionResult retVal = new GetSolutionResult();
            Solution solution = null;

            // generate key based on output values 
            long solutionId = Util.GenerateHashKey(outputs.Select(a => a.Value).ToArray());

            // create new solution if not exists
            if (!Solutions.TryGetValue(solutionId, out solution))
            {
                solution = new Solution() { ID = solutionId };

                foreach(var o in outputs)
                {
                    // create OVS instance
                    var ovs = new Output_Values_Solution()
                    {
                        ID = Util.GenerateHashKey(solutionId, o.ID),
                        Value = o.Value,
                        Output_ID = o.ID,
                        Solution_ID = solutionId
                    };
                    solution.Output_Values_Solutions.Add(ovs);

                    // insert into dynamic output collection
                    HashSet<SolutionOutputSet> outputSet = DynamicOutputs[o.ID];
                    outputSet.Add(new SolutionOutputSet()
                    {
                        SolutionId = solutionId,
                        Value = o.Value
                    });
                }

                Solutions.TryAdd(solution.ID, solution);

                //Solutions2.Enqueue(retVal);

                //solution_queue.Add(retVal);

                retVal.Solution = solution;
                retVal.ExistsInCache = false;
            }
            else
            {
                retVal.Solution = solution;
                retVal.ExistsInCache = true;
            }

            return retVal; 
        }
        /// <summary>
        /// Sets solution and cache
        /// </summary>
        /// <param name="solution">solution</param>
        public void SetSolutionWithOutputs(Solution solution)
        {
            long solutionId = solution.ID;

            // add to Solutions cache
            Solutions.TryAdd(solution.ID, solution);

            // build dynamic outputs
            foreach (var o in solution.Output_Values_Solutions)
            {
                // insert into dynamic output collection
                HashSet<SolutionOutputSet> outputSet = DynamicOutputs[o.Output_ID];
                outputSet.Add(new SolutionOutputSet()
                {
                    SolutionId = solutionId,
                    Value = o.Value
                });
            }              
        }


        /// <summary>
        /// starts database workers that handle queue's
        /// </summary>
        public void StartRlmDbWorkers()
        {
            //note: we can start multiple workers later
            sessionCreateTask = rlmDb.StartSessionWorkerForCreate(session_queue, tokenSessions); //start session thread for create
            Task.Run(() => { rlmDbEnqueuer.QueueObjects<Session>(Sessions2, session_queue); }); //queue sessions for create to blocking collections

            sessionUpdateTask = rlmDb.StartSessionWorkerForUpdate(savedSession_queue, tokenSessions); //start session thread for update
            Task.Run(() => { rlmDbEnqueuer.QueueObjects<Session>(Sessions3, savedSession_queue); }); //queue sessions for update to blocking collections

            caseTask = rlmDb.StartCaseWorker(case_queue, tokenCases); //start case thread to save (Rneuron, Solution, Case) to db
            //Task.Run(() => { rlmDbEnqueuer.QueueObjects<Case>(Cases2, case_queue); }); //queue case values to blocking collections                     
            Task.Run(() => { rlmDbEnqueuer.QueueObjects(MASTER_CASE_QUEUE, case_queue, caseQueue_lock); });

            progressUpdater.Start();

            //rnnDb.StartCaseWorker(tokenCases);
        }

        /// <summary>
        /// stops the session queue worker
        /// </summary>
        public void StopRlmDbWorkersSessions()
        {
            session_queue.CompleteAdding();
            savedSession_queue.CompleteAdding();

            ctSourceSessions.Cancel();

            sessionsDone = true;
            totalSessionsCount = Sessions.Count;
        }
        /// <summary>
        /// stops the cases queue worker
        /// </summary>
        public void StopRlmDbWorkersCases()
        {
            case_queue.CompleteAdding();
            
            ctSourceCases.Cancel();

            dbSavingTime.Stop();

            RlmDbLogger.Info("\n" + string.Format("[{0:G}]: Data successfully saved to the database in {1}", DateTime.Now, dbSavingTime.Elapsed), Network.DatabaseName);
            if (ConfigFile.DropDb)
            {
                Task.Delay(5000).Wait();

                rlmDb.DropDB();

                RlmDbLogger.Info("\n" + string.Format("[{0:G}]: {1} database successfully dropped...\n*** END ***\n", DateTime.Now, Network.DatabaseName), Network.DatabaseName);
            }

            // notify parent network that db background workers are done
            DataPersistenceComplete?.Invoke();
            progressUpdater.Stop();
            
            foreach (var item in rlmDb.CaseWorkerQueues)
            {
                item.CompleteAdding();
            }
        }
        /// <summary>
        /// Loads the network result
        /// </summary>
        /// <param name="network"></param>
        /// <returns></returns>
        public LoadRnetworkResult LoadNetwork(string networkName)
        {
            var result = rlmDb.LoadNetwork(networkName, Network);
            if (result.Loaded && Network.PersistData)
            {
                StartRlmDbWorkers();
            }
            return result;
        }

        /// <summary>
        /// signals that training is done
        /// </summary>
        public void TrainingDone()
        {
            trainingDone = true;

            //background thread to stop session db workers when done
            Task.Run(() =>
            {
                while (true)
                {
                    bool processing = Sessions.Any(a => a.Value.CreatedToDb == false || a.Value.UpdatedToDb == false);
                    if (!processing && /*Sessions.Count > 0 &&*/ trainingDone)
                    {
                        StopRlmDbWorkersSessions();
                        System.Diagnostics.Debug.WriteLine("Worker Session done");
                        break;
                    }

                    Task.Delay(5 * 1000).Wait();
                }

            });

            //background thread to stop case db workers when done
            Task.Run(() =>
            {
                while (true)
                {
                    ////bool processing = Cases.Any(a => a.SavedToDb == false);
                    //if (/*!processing &&*/ Cases.Count == 0 && trainingDone && sessionsDone && rlmDb.DistinctCaseSessionsCount() == totalSessionsCount)
                    //{
                    //    StopRlmDbWorkersCases();
                    //    System.Diagnostics.Debug.WriteLine("Worker Cases done");
                    //    break;
                    //}

                    if (sessionsDone && 
                        //rlmDb.DistinctCaseSessionsCount() == totalSessionsCount && 
                        MASTER_CASE_QUEUE.Count == 1 && 
                        MASTER_CASE_QUEUE.ElementAt(0).Count == 0)
                    {
                        StopRlmDbWorkersCases();
                        System.Diagnostics.Debug.WriteLine("Worker Cases done");
                        break;
                    }

                    Task.Delay(5 * 1000).Wait();
                }

            });
        }

        public void SetProgressInterval(int interval)
        {
            progressUpdater.Interval = interval;
        }

        private void ProgressUpdater_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (lastProgress != rlmDb.TotalTaskCompleted)
            {
                lastProgress = rlmDb.TotalTaskCompleted;
                DataPersistenceProgress?.Invoke(rlmDb.TotalTaskCompleted, rlmDbEnqueuer.TotalTaskEnqueued);
            }
        }

        private IEnumerable<RlmIOWithValue> GetRandomOutputValues(IEnumerable<RlmIO> outputs)
        {
            var retVal = new List<RlmIOWithValue>();

            foreach (var item in outputs)
            {
                string value = GetRandomValue(item);

                var output_with_value = new RlmIOWithValue(item, value);
                retVal.Add(output_with_value);
            }

            return retVal;
        }

        private string GetRandomValue(RlmIO item)
        {
            string value = string.Empty;

            // TODO add checking on the types and their max and min values
            switch (item.DotNetType)
            {
                case "System.Boolean":
                    int boolMin = Convert.ToInt32(item.Min);
                    int boolMax = Convert.ToInt32(item.Max + 1);
                    int boolIntValue = Util.Randomizer.Next(boolMin, boolMax);
                    if (boolIntValue > 1 && boolIntValue < 0)
                    {
                        throw new Exception("Boolean value can only be 1 or 0");
                    }
                    value = Convert.ToBoolean(boolIntValue).ToString();
                    break;

                case "System.Double":
                case "System.Decimal":
                    double doubleMin = Convert.ToDouble(item.Min);
                    double doubleMax = Convert.ToDouble(item.Max);
                    value = Util.Randomizer.NextDouble(doubleMin, doubleMax).ToString();
                    break;

                default:
                    int min = Convert.ToInt32(item.Min);
                    int max = Convert.ToInt32(item.Max + 1);
                    if (item.Idea != null)
                    {
                        max = item.Idea.IndexMax + 1;
                        int ideaIndex = Util.Randomizer.Next(min, max);
                        if (item.Idea.GetIndexEquivalent != null)
                        {
                            value = item.Idea.GetIndexEquivalent(ideaIndex).ToString();
                        }
                        else
                        {
                            value = ideaIndex.ToString();
                        }
                    }
                    else
                    {
                        value = Util.Randomizer.Next(min, max).ToString();
                    }
                    break;
            }

            return value;
        }
    }
}
