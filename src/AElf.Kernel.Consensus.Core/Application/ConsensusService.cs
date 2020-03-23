﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acs4;
using AElf.CSharp.Core.Extension;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.Consensus.Application
{
    internal class ConsensusService : IConsensusService, ISingletonDependency
    {
        private ConsensusCommand _consensusCommand;
        private readonly IConsensusScheduler _consensusScheduler;
        private readonly IConsensusReaderFactory _readerFactory;
        private readonly ITriggerInformationProvider _triggerInformationProvider;
        private readonly IBlockTimeProvider _blockTimeProvider;
        public ILocalEventBus LocalEventBus { get; set; }

        public ILogger<ConsensusService> Logger { get; set; }

        private Timestamp _nextMiningTime;

        public ConsensusService(IConsensusScheduler consensusScheduler,
            IConsensusReaderFactory readerFactory,
            ITriggerInformationProvider triggerInformationProvider,
            IBlockTimeProvider blockTimeProvider)
        {
            _readerFactory = readerFactory;
            _triggerInformationProvider = triggerInformationProvider;
            _blockTimeProvider = blockTimeProvider;
            _consensusScheduler = consensusScheduler;

            Logger = NullLogger<ConsensusService>.Instance;
            LocalEventBus = NullLocalEventBus.Instance;
        }

        /// <summary>
        /// Basically update the consensus scheduler with latest consensus command.
        /// </summary>
        /// <param name="chainContext"></param>
        /// <returns></returns>
        public async Task TriggerConsensusAsync(ChainContext chainContext)
        {
            var now = TimestampHelper.GetUtcNow();
            _blockTimeProvider.SetBlockTime(now);

            Logger.LogTrace($"Set block time to utc now: {now.ToDateTime():hh:mm:ss.ffffff}. Trigger.");

            var triggerInformation =
                _triggerInformationProvider.GetTriggerInformationForConsensusCommand(new BytesValue());

            Logger.LogDebug($"Mining triggered, chain context: {chainContext.BlockHeight} - {chainContext.BlockHash}");

            // Upload the consensus command.
            _consensusCommand = await _readerFactory.Create(chainContext)
                .GetConsensusCommand.CallAsync(triggerInformation);

            if (_consensusCommand == null)
            {
                Logger.LogWarning("Consensus command is null.");
                return;
            }

            Logger.LogDebug($"Updated consensus command: {_consensusCommand}");

            // Update next mining time, also block time of both getting consensus extra data and txs.
            _nextMiningTime = _consensusCommand.ArrangedMiningTime;
            var leftMilliseconds = _consensusCommand.ArrangedMiningTime - TimestampHelper.GetUtcNow();
            leftMilliseconds = leftMilliseconds.Seconds > ConsensusConstants.MaximumLeftMillisecondsForNextBlock
                ? new Duration {Seconds = ConsensusConstants.MaximumLeftMillisecondsForNextBlock}
                : leftMilliseconds;

            // Update consensus scheduler.
            var blockMiningEventData = new ConsensusRequestMiningEventData(chainContext.BlockHash,
                chainContext.BlockHeight,
                _nextMiningTime,
                TimestampHelper.DurationFromMilliseconds(_consensusCommand.LimitMillisecondsOfMiningBlock),
                _consensusCommand.MiningDueTime);
            _consensusScheduler.CancelCurrentEvent();
            _consensusScheduler.NewEvent(leftMilliseconds.Milliseconds(), blockMiningEventData);

            Logger.LogTrace($"Set next mining time to: {_nextMiningTime.ToDateTime():hh:mm:ss.ffffff}");
        }

        /// <summary>
        /// Call ACS4 method ValidateConsensusBeforeExecution.
        /// </summary>
        /// <param name="chainContext"></param>
        /// <param name="consensusExtraData"></param>
        /// <returns></returns>
        public async Task<bool> ValidateConsensusBeforeExecutionAsync(ChainContext chainContext,
            byte[] consensusExtraData)
        {
            var now = TimestampHelper.GetUtcNow();
            _blockTimeProvider.SetBlockTime(now);

            Logger.LogTrace($"Set block time to utc now: {now.ToDateTime():hh:mm:ss.ffffff}. Validate Before.");

            var validationResult = await _readerFactory.Create(chainContext).ValidateConsensusBeforeExecution
                .CallAsync(new BytesValue {Value = ByteString.CopyFrom(consensusExtraData)});

            if (validationResult == null)
            {
                Logger.LogWarning("Validation of consensus failed before execution.");
                return false;
            }

            if (!validationResult.Success)
            {
                Logger.LogWarning($"Consensus validating before execution failed: {validationResult.Message}");
                await LocalEventBus.PublishAsync(new ConsensusValidationFailedEventData
                {
                    ValidationResultMessage = validationResult.Message,
                    IsReTrigger = validationResult.IsReTrigger
                });
            }

            return validationResult.Success;
        }

        /// <summary>
        /// Call ACS4 method ValidateConsensusAfterExecution.
        /// </summary>
        /// <param name="chainContext"></param>
        /// <param name="consensusExtraData"></param>
        /// <returns></returns>
        public async Task<bool> ValidateConsensusAfterExecutionAsync(ChainContext chainContext,
            byte[] consensusExtraData)
        {
            var now = TimestampHelper.GetUtcNow();
            _blockTimeProvider.SetBlockTime(now);

            Logger.LogTrace($"Set block time to utc now: {now.ToDateTime():hh:mm:ss.ffffff}. Validate After.");

            var validationResult = await _readerFactory.Create(chainContext).ValidateConsensusAfterExecution
                .CallAsync(new BytesValue {Value = ByteString.CopyFrom(consensusExtraData)});

            if (validationResult == null)
            {
                Logger.LogWarning("Validation of consensus failed after execution.");
                return false;
            }

            if (!validationResult.Success)
            {
                Logger.LogWarning($"Consensus validating after execution failed: {validationResult.Message}");
                await LocalEventBus.PublishAsync(new ConsensusValidationFailedEventData
                {
                    ValidationResultMessage = validationResult.Message,
                    IsReTrigger = validationResult.IsReTrigger
                });
            }

            return validationResult.Success;
        }

        /// <inheritdoc />
        /// <summary>
        /// Get consensus block header extra data.
        /// </summary>
        /// <param name="chainContext"></param>
        /// <returns></returns>
        public async Task<byte[]> GetConsensusExtraDataAsync(ChainContext chainContext)
        {
            _blockTimeProvider.SetBlockTime(_nextMiningTime);

            Logger.LogTrace(
                $"Set block time to next mining time: {_nextMiningTime.ToDateTime():hh:mm:ss.ffffff}. Extra Data.");

            return (await _readerFactory.Create(chainContext).GetConsensusExtraData
                    .CallAsync(_triggerInformationProvider.GetTriggerInformationForBlockHeaderExtraData(
                        _consensusCommand.ToBytesValue()))).Value
                .ToByteArray();
        }

        /// <summary>
        /// Get consensus system tx list.
        /// </summary>
        /// <param name="chainContext"></param>
        /// <returns></returns>
        public async Task<List<Transaction>> GenerateConsensusTransactionsAsync(ChainContext chainContext)
        {
            _blockTimeProvider.SetBlockTime(_nextMiningTime);

            Logger.LogTrace(
                $"Set block time to next mining time: {_nextMiningTime.ToDateTime():hh:mm:ss.ffffff}. Txs.");

            var generatedTransactions =
                (await _readerFactory.Create(chainContext).GenerateConsensusTransactions
                    .CallAsync(_triggerInformationProvider.GetTriggerInformationForConsensusTransactions(
                        _consensusCommand.ToBytesValue())))
                .Transactions
                .ToList();

            // Complete these transactions.
            foreach (var generatedTransaction in generatedTransactions)
            {
                generatedTransaction.RefBlockNumber = chainContext.BlockHeight;
                generatedTransaction.RefBlockPrefix =
                    ByteString.CopyFrom(chainContext.BlockHash.Value.Take(4).ToArray());
                Logger.LogInformation($"Consensus transaction generated: \n{generatedTransaction.GetHash()}");
            }

            return generatedTransactions;
        }
    }
}