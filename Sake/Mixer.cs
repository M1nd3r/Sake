﻿using NBitcoin;
using NBitcoin.Policy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WalletWasabi.Extensions;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using static System.Net.WebRequestMethods;

namespace Sake
{
    internal class Mixer
    {

        /// <param name="feeRate">Bitcoin network fee rate the coinjoin is targeting.</param>
        /// <param name="minAllowedOutputAmount">Minimum output amount that's allowed to be registered.</param>
        /// <param name="maxAllowedOutputAmount">Miximum output amount that's allowed to be registered.</param>
        /// <param name="allowedOutputTypes">Allouwed output scriot types.</param>
        /// <param name="random">Random numbers generator.</param>
        public Mixer(FeeRate feeRate, Money minAllowedOutputAmount, Money maxAllowedOutputAmount, IEnumerable<ScriptType> allowedOutputTypes, Random? random = null)
        {
            MiningFeeRate = feeRate;
            AllowedOutputTypes = allowedOutputTypes;
            MinAllowedOutputAmount = CalculateMinReasonableOutputAmount(minAllowedOutputAmount); // In WalletWasabi, this calculation happens outside of the AmountDecomposer.
            MaxAllowedOutputAmount = maxAllowedOutputAmount;
            Random = random ?? Random.Shared;

            // Create many standard denominations.
            Denominations = DenominationBuilder.CreateDenominations(MinAllowedOutputAmount, MaxAllowedOutputAmount, MiningFeeRate, AllowedOutputTypes, Random);
            ChangeScriptType = AllowedOutputTypes.RandomElement(Random);
        }
        private int MaxVsizeInputOutputPair => AllowedOutputTypes.Max(x => x.EstimateInputVsize() + x.EstimateOutputVsize());
        private ScriptType MaxVsizeInputOutputPairScriptType => AllowedOutputTypes.MaxBy(x => x.EstimateInputVsize() + x.EstimateOutputVsize());

        public ScriptType ChangeScriptType { get; }
        public Money ChangeFee => MiningFeeRate.GetFee(ChangeScriptType.EstimateOutputVsize());
        public Money MinAllowedOutputAmount { get; }
        public Money MaxAllowedOutputAmount { get; }
        private Random Random { get; }

        public FeeRate MiningFeeRate { get; }
        public IEnumerable<ScriptType> AllowedOutputTypes { get; }
        public int InputSize { get; } = 69;
        public int OutputSize { get; } = 33;
        public List<int> Leftovers { get; } = new();
        public IOrderedEnumerable<Output> Denominations { get; }
        public List<Output> Outputs { get; } = new();

        /// <summary>
        /// Run a series of mix with different input group combinations. 
        /// </summary>
        /// <param name="inputs">Input effective values. The fee substracted, this is how the code works in the original repo.</param>
        /// <returns></returns>
        public IEnumerable<IEnumerable<ulong>> CompleteMix(IEnumerable<IEnumerable<Input>> inputs)
        {
            var inputArray = inputs.ToArray();
            var allInputsEffectiveValue = inputArray.SelectMany(x => x).Select(x => (ulong)x.EffectiveValue.Satoshi).ToArray();

            var filteredDenominations = GetFilteredDenominations(allInputsEffectiveValue);

            var totalInputCount = allInputsEffectiveValue.Length;

            // This calculation is coming from here: https://github.com/zkSNACKs/WalletWasabi/blob/8b3fb65b/WalletWasabi/WabiSabi/Backend/Rounds/RoundParameters.cs#L48
            StandardTransactionPolicy standardTransactionPolicy = new();
            var maxTransactionSize = standardTransactionPolicy.MaxTransactionSize ?? 100_000;
            var initialInputVsizeAllocation = maxTransactionSize - MultipartyTransactionParameters.SharedOverhead;

            // If we are not going up with the number of inputs above ~400, vsize per alice will be 255. 
            var maxVsizeCredentialValue = Math.Min(initialInputVsizeAllocation / totalInputCount, (int)ProtocolConstants.MaxVsizeCredentialValue);

            for (int i = 0; i < inputArray.Length; i++)
            {
                var currentUser = inputArray[i];

                // Calculated totalVsize that we can use. https://github.com/zkSNACKs/WalletWasabi/blob/8b3fb65b/WalletWasabi/WabiSabi/Client/AliceClient.cs#L157
                var availableVsize = currentUser.Sum(input => maxVsizeCredentialValue - input.ScriptType.EstimateInputVsize());

                var others = new List<Input>();
                for (int j = 0; j < inputArray.Length; j++)
                {
                    if (i != j)
                    {
                        others.AddRange(inputArray[j]);
                    }
                }
                yield return Decompose(currentUser.Select(x => x.EffectiveValue), filteredDenominations, availableVsize).Select(d => (ulong)d.Amount.Satoshi);
            }
        }

        /// <param name="myInputsParam">Input effective values. The fee substracted, this is how the code works in the original repo.</param>
        /// <param name="availableVsize">Calculated totalVsize that we can use for the outputs..</param>
        public IEnumerable<Output> Decompose(IEnumerable<Money> myInputsParam, IEnumerable<Output> denoms, int availableVsize)
        {
            var myInputs = myInputsParam.ToArray();
            var myInputSum = myInputs.Sum();
            var smallestScriptType = Math.Min(ScriptType.P2WPKH.EstimateOutputVsize(), ScriptType.Taproot.EstimateOutputVsize());
            var maxNumberOfOutputsAllowed = Math.Min(availableVsize / smallestScriptType, 10); // The absolute max possible with the smallest script type.

            // If my input sum is smaller than the smallest denomination, then participation in a coinjoin makes no sense.
            if (denoms.Min(x => x.EffectiveCost) > myInputSum)
            {
                throw new InvalidOperationException("Not enough coins registered to participate in the coinjoin.");
            }

            var setCandidates = new Dictionary<int, (IEnumerable<Output> Decomp, Money Cost)>();

            // Create the most naive decomposition for starter.
            var naiveDecomp = CreateNaiveDecomposition(denoms, availableVsize, myInputSum, maxNumberOfOutputsAllowed);
            setCandidates.Add(naiveDecomp.Key, naiveDecomp.Value);

            // Create more pre-decompositions for sanity.
            var preDecomps = CreatePreDecompositions(denoms, availableVsize, myInputSum, maxNumberOfOutputsAllowed);
            foreach (var decomp in preDecomps)
            {
                setCandidates.TryAdd(decomp.Key, decomp.Value);
            }

            // Create many decompositions for optimization.
            var changelessDecomps = CreateChangelessDecompositions(denoms, availableVsize, myInputSum, maxNumberOfOutputsAllowed);
            foreach (var decomp in changelessDecomps)
            {
                setCandidates.TryAdd(decomp.Key, decomp.Value);
            }

            var denomHashSet = denoms.ToHashSet();

            var preCandidates = setCandidates.Select(x => x.Value).ToList();

            // If there are changeless candidates, don't even consider ones with change.
            var changelessCandidates = preCandidates.Where(x => x.Decomp.All(y => denomHashSet.Contains(y))).ToList();
            var changeAvoided = changelessCandidates.Any();
            if (changeAvoided)
            {
                preCandidates = changelessCandidates;
            }
            preCandidates.Shuffle();

            var orderedCandidates = preCandidates
                .OrderBy(x => x.Decomp.Sum(y => denomHashSet.Contains(y) ? Money.Zero : y.Amount)) // Less change is better.
                .ThenBy(x => x.Cost) // Less cost is better.
                .ThenBy(x => x.Decomp.Any(d => d.ScriptType == ScriptType.Taproot) && x.Decomp.Any(d => d.ScriptType == ScriptType.P2WPKH) ? 0 : 1) // Prefer mixed scripts types.
                .Select(x => x).ToList();

            // We want to introduce randomness between the best selections.
            // If we successfully avoided change, then what matters is cost,
            // if we didn't then cost calculation is irrelevant, because the size of change is more costly.
            (IEnumerable<Output> Decomp, Money Cost)[] finalCandidates;
            if (changeAvoided)
            {
                var bestCandidateCost = orderedCandidates.First().Cost;
                var costTolerance = Money.Coins(bestCandidateCost.ToUnit(MoneyUnit.BTC) * 1.2m);
                finalCandidates = orderedCandidates.Where(x => x.Cost <= costTolerance).ToArray();
            }
            else
            {
                // Change can only be max between: 100.000 satoshis, 10% of the inputs sum or 20% more than the best candidate change
                var bestCandidateChange = FindChange(orderedCandidates.First().Decomp, denomHashSet);
                var changeTolerance = Money.Coins(
                    Math.Max(
                        Math.Max(
                            myInputSum.ToUnit(MoneyUnit.BTC) * 0.1m,
                            bestCandidateChange.ToUnit(MoneyUnit.BTC) * 1.2m),
                        Money.Satoshis(100000).ToUnit(MoneyUnit.BTC)));

                finalCandidates = orderedCandidates.Where(x => FindChange(x.Decomp, denomHashSet) <= changeTolerance).ToArray();
            }

            // We want to make sure our random selection is not between similar decompositions.
            // Different largest elements result in very different decompositions.
            var largestAmount = finalCandidates.Select(x => x.Decomp.First()).ToHashSet().RandomElement(Random);
            var finalCandidate = finalCandidates.Where(x => x.Decomp.First() == largestAmount).RandomElement(Random).Decomp;

            // Sanity check
            var totalOutputAmount = Money.Satoshis(finalCandidate.Sum(x => x.EffectiveCost));
            if (totalOutputAmount > myInputSum)
            {
                throw new InvalidOperationException("The decomposer is creating money. Aborting.");
            }
            if (totalOutputAmount + MinAllowedOutputAmount + ChangeFee < myInputSum)
            {
                throw new InvalidOperationException("The decomposer is losing money. Aborting.");
            }

            var totalOutputVsize = finalCandidate.Sum(d => d.ScriptType.EstimateOutputVsize());
            if (totalOutputVsize > availableVsize)
            {
                throw new InvalidOperationException("The decomposer created more outputs than it can. Aborting.");
            }

            var leftover = myInputSum - totalOutputAmount;
            if (leftover > MinAllowedOutputAmount + ChangeFee)
            {
                throw new NotSupportedException($"Leftover too large. Aborting to avoid money loss: {leftover}");
            }
            Leftovers.Add((int)leftover);

            Outputs.AddRange(finalCandidate);

            return finalCandidate;
        }

        private static Money FindChange(IEnumerable<Output> decomposition, HashSet<Output> denomHashSet)
        {
            return decomposition.Sum(x => denomHashSet.Contains(x) ? Money.Zero : x.Amount);
        }

        private IDictionary<int, (IEnumerable<Output> Decomp, Money Cost)> CreateChangelessDecompositions(IEnumerable<Output> denoms, int availableVsize, Money myInputSum, int maxNumberOfOutputsAllowed)
        {
            var setCandidates = new Dictionary<int, (IEnumerable<Output> Decomp, Money Cost)>();

            var stdDenoms = denoms.Select(x => x.EffectiveCost.Satoshi).Where(x => x <= myInputSum).Select(x => x).ToArray();

            if (maxNumberOfOutputsAllowed > 1)
            {
                foreach (var (sum, count, decomp) in Decomposer.Decompose(
                    target: (long)myInputSum,
                    tolerance: MinAllowedOutputAmount + ChangeFee,
                    maxCount: Math.Min(maxNumberOfOutputsAllowed, 8), // Decomposer doesn't do more than 8.
                    stdDenoms: stdDenoms))
                {
                    var currentSet = Decomposer.ToRealValuesArray(
                                            decomp,
                                            count,
                                            stdDenoms).Select(Money.Satoshis).ToList();

                    // Translate back to denominations.
                    List<Output> finalDenoms = new();
                    foreach (var outputPlusFee in currentSet)
                    {
                        finalDenoms.Add(denoms.First(d => d.EffectiveCost == outputPlusFee));
                    }

                    // The decomposer won't take vsize into account for different script types, checking it back here if too much, disregard the decomposition.
                    var totalVSize = finalDenoms.Sum(d => d.ScriptType.EstimateOutputVsize());
                    if (totalVSize > availableVsize)
                    {
                        continue;
                    }

                    var deficit = (myInputSum - (ulong)finalDenoms.Sum(d => d.EffectiveCost)) + CalculateCost(finalDenoms);
                    setCandidates.TryAdd(CalculateHash(finalDenoms), (finalDenoms, deficit));
                }
            }

            return setCandidates;
        }

        private IDictionary<int, (IEnumerable<Output> Decomp, Money Cost)> CreatePreDecompositions(IEnumerable<Output> denoms, int availableVsize, Money myInputSum, int maxNumberOfOutputsAllowed)
        {
            var setCandidates = new Dictionary<int, (IEnumerable<Output> Decomp, Money Cost)>();

            for (int i = 0; i < 100; i++)
            {
                var remainingVsize = availableVsize;
                var remaining = myInputSum;
                List<Output> currentSet = new();
                while (true)
                {
                    var denom = denoms.Where(x => x.EffectiveCost <= remaining && x.EffectiveCost >= (remaining / 3)).RandomElement(Random)
                        ?? denoms.FirstOrDefault(x => x.EffectiveCost <= remaining);

                    // Continue only if there is enough remaining amount and size to create one output (+ change if change could potentially be created).
                    // There can be change only if the remaining is at least the current denom effective cost + the minimum change effective cost.
                    if (denom is null ||
                        (remaining < denom.EffectiveCost + MinAllowedOutputAmount + ChangeFee && remainingVsize < denom.ScriptType.EstimateOutputVsize()) ||
                        (remaining >= denom.EffectiveCost + MinAllowedOutputAmount + ChangeFee && remainingVsize < denom.ScriptType.EstimateOutputVsize() + ChangeScriptType.EstimateOutputVsize()))
                    {
                        break;
                    }

                    currentSet.Add(denom);
                    remaining -= denom.EffectiveCost;
                    remainingVsize -= denom.ScriptType.EstimateOutputVsize();

                    // Can't have more denoms than max - 1, where -1 is to account for possible change.
                    if (currentSet.Count >= maxNumberOfOutputsAllowed - 1)
                    {
                        break;
                    }
                }

                var loss = Money.Zero;
                if (remaining >= MinAllowedOutputAmount + ChangeFee)
                {
                    var change = Output.FromAmount(remaining, ChangeScriptType, MiningFeeRate);
                    currentSet.Add(change);
                }
                else
                {
                    // This goes to miners.
                    loss = remaining;
                }

                setCandidates.TryAdd(
                    CalculateHash(currentSet), // Create hash to ensure uniqueness.
                    (currentSet, loss + CalculateCost(currentSet)));
            }

            return setCandidates;
        }

        private KeyValuePair<int, (IEnumerable<Output> Decomp, Money Cost)> CreateNaiveDecomposition(IEnumerable<Output> denoms, int availableVsize, Money myInputSum, int maxNumberOfOutputsAllowed)
        {
            var remainingVsize = availableVsize;
            var remaining = myInputSum;

            List<Output> naiveSet = new();
            foreach (var denom in denoms.Where(x => x.EffectiveCost <= remaining))
            {
                bool end = false;
                while (denom.EffectiveCost <= remaining)
                {
                    // Continue only if there is enough remaining amount and size to create one output + change (if change will potentially be created).
                    // There can be change only if the remaining is at least the current denom effective cost + the minimum change effective cost.
                    if ((remaining < denom.EffectiveCost + MinAllowedOutputAmount + ChangeFee && remainingVsize < denom.ScriptType.EstimateOutputVsize()) ||
                        (remaining >= denom.EffectiveCost + MinAllowedOutputAmount + ChangeFee && remainingVsize < denom.ScriptType.EstimateOutputVsize() + ChangeScriptType.EstimateOutputVsize()))
                    {
                        end = true;
                        break;
                    }

                    naiveSet.Add(denom);
                    remaining -= denom.EffectiveCost;
                    remainingVsize -= denom.ScriptType.EstimateOutputVsize();

                    // Can't have more denoms than max - 1, where -1 is to account for possible change.
                    if (naiveSet.Count >= maxNumberOfOutputsAllowed - 1)
                    {
                        end = true;
                        break;
                    }
                }

                if (end)
                {
                    break;
                }
            }

            var loss = Money.Zero;
            if (remaining >= MinAllowedOutputAmount + ChangeFee)
            {
                var change = Output.FromAmount(remaining, ChangeScriptType, MiningFeeRate);
                naiveSet.Add(change);
            }
            else
            {
                // This goes to miners.
                loss = remaining;
            }

            // This can happen when smallest denom is larger than the input sum.
            if (naiveSet.Count == 0)
            {
                var change = Output.FromAmount(remaining, ChangeScriptType, MiningFeeRate);
                naiveSet.Add(change);
            }

            return KeyValuePair.Create(CalculateHash(naiveSet), ((IEnumerable<Output>)naiveSet, loss + CalculateCost(naiveSet)));
        }

        private IEnumerable<Output> GetFilteredDenominations(IEnumerable<ulong> inputs)
        {
            var secondLargestInput = inputs.OrderByDescending(x => x).Skip(1).First();
            IEnumerable<Output> demonsForBreakDown = Denominations
                .Where(x => x.EffectiveCost <= secondLargestInput)
                .OrderByDescending(x => x.EffectiveAmount); // If the amount is the same, the cheaper to spend should be the first - so greedy will take that.

            Dictionary<Output, uint> denoms = new();
            foreach (var input in inputs)
            {
                foreach (var denom in BreakDown(input, demonsForBreakDown))
                {
                    if (!denoms.TryAdd(denom, 1))
                    {
                        denoms[denom]++;
                    }
                }
            }

            // Filter out and order denominations those have occured in the frequency table at least twice.
            var preFilteredDenoms = denoms
                .Where(x => x.Value > 1)
                .OrderByDescending(x => x.Key.EffectiveCost)
                .Select(x => x.Key)
            .ToArray();

            // Filter out denominations very close to each other.
            // Heavy filtering on the top, little to no filtering on the bottom,
            // because in smaller denom levels larger users are expected to participate,
            // but on larger denom levels there's little chance of finding each other.
            var increment = 0.5 / preFilteredDenoms.Length;
            List<Output> lessDenoms = new();
            var currentLength = preFilteredDenoms.Length;
            foreach (var denom in preFilteredDenoms)
            {
                var filterSeverity = 1 + currentLength * increment;
                if (!lessDenoms.Any() || denom.Amount.Satoshi <= (lessDenoms.Last().Amount.Satoshi / filterSeverity))
                {
                    lessDenoms.Add(denom);
                }
                currentLength--;
            }

            return lessDenoms;
        }

        /// <summary>
        /// Greedily decomposes an amount to the given denominations.
        /// </summary>
        private IEnumerable<Output> BreakDown(Money input, IEnumerable<Output> denominations)
        {
            var remaining = input;

            foreach (var denom in denominations)
            {
                if (denom.Amount < MinAllowedOutputAmount || remaining < MinAllowedOutputAmount + ChangeFee)
                {
                    break;
                }

                while (denom.EffectiveCost <= remaining)
                {
                    yield return denom;
                    remaining -= denom.EffectiveCost;
                }
            }

            if (remaining >= MinAllowedOutputAmount + ChangeFee)
            {
                var changeOutput = Output.FromAmount(remaining, ScriptType.P2WPKH, MiningFeeRate);
                yield return changeOutput;
            }
        }

        public static Money CalculateCost(IEnumerable<Output> outputs)
        {
            // The cost of the outputs. The more the worst.
            var outputCost = outputs.Sum(o => o.Fee);

            // The cost of sending further or remix these coins.
            var inputCost = outputs.Sum(o => o.InputFee);

            return outputCost + inputCost;
        }

        private int CalculateHash(IEnumerable<Output> outputs)
        {
            HashCode hash = new();
            foreach (var item in outputs.OrderBy(x => x.EffectiveCost))
            {
                hash.Add(item.Amount);
            }
            return hash.ToHashCode();
        }

        /// <returns>Min output amount that's economically reasonable to be registered with current network conditions.</returns>
        /// <remarks>It won't be smaller than min allowed output amount.</remarks>
        public Money CalculateMinReasonableOutputAmount(Money minAllowedOutputAmount)
        {
            var minEconomicalOutput = MiningFeeRate.GetFee(MaxVsizeInputOutputPair);
            return Math.Max(minEconomicalOutput, minAllowedOutputAmount);
        }

        public Money CalculateSmallestReasonableEffectiveDenomination()
            => CalculateSmallestReasonableEffectiveDenomination(MinAllowedOutputAmount, MaxAllowedOutputAmount, MiningFeeRate, MaxVsizeInputOutputPairScriptType);

        /// <returns>Smallest effective denom that's larger than min reasonable output amount. </returns>
        public static Money CalculateSmallestReasonableEffectiveDenomination(Money minReasonableOutputAmount, Money maxAllowedOutputAmount, FeeRate feeRate, ScriptType maxVsizeInputOutputPairScriptType)
        {
            var smallestEffectiveDenom = DenominationBuilder.CreateDenominations(
                    minReasonableOutputAmount,
                    maxAllowedOutputAmount,
                    feeRate,
                    new List<ScriptType>() { maxVsizeInputOutputPairScriptType })
                .Min(x => x.EffectiveCost);

            return smallestEffectiveDenom is null
                ? throw new InvalidOperationException("Something's wrong with the denomination creation or with the parameters it got.")
                : smallestEffectiveDenom;
        }

        internal IEnumerable<IEnumerable<Input>> RandomInputGroups(IEnumerable<Input> preRandomAmounts, int userCount)
        {
            var smallestEffectiveDenom = CalculateSmallestReasonableEffectiveDenomination();
            for (int i = 0; i < 1000; i++)
            {
                var randomGroups = preRandomAmounts.RandomGroups(userCount);
                bool cont = false;

                foreach (var currentUser in randomGroups)
                {
                    var effectiveInputSum = currentUser.Sum(x => x.EffectiveValue);

                    // If we find such things, then it's wrong randomization, try again.
                    if (effectiveInputSum < smallestEffectiveDenom)
                    {
                        cont = true;
                        break;
                    }
                }

                if (cont)
                {
                    continue;
                }

                return randomGroups;
            }

            throw new InvalidOperationException("Couldn't find randomization of input groups where each are large enough.");
        }
    }
}
