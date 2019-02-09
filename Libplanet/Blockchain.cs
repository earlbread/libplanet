using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Libplanet.Action;
using Libplanet.Blocks;
using Libplanet.Store;
using Libplanet.Tx;

[assembly: InternalsVisibleTo("Libplanet.Tests")]
namespace Libplanet
{
    public class Blockchain<T> : IEnumerable<Block<T>>
        where T : IAction
    {
        private static readonly TimeSpan BlockInterval = new TimeSpan(
            hours: 0,
            minutes: 0,
            seconds: 5
        );

        public Blockchain(IStore store)
        {
            Store = store;
            Blocks = new BlockSet<T>(store);
            Transactions = new TransactionSet<T>(store);
            Addresses = new AddressTransactionSet<T>(store);
        }

        public IDictionary<HashDigest<SHA256>, Block<T>> Blocks { get; }

        public IDictionary<TxId, Transaction<T>> Transactions { get; }

        public IDictionary<Address, IEnumerable<Transaction<T>>> Addresses
        {
            get;
        }

        public IStore Store { get; }

        public Block<T> this[long index]
        {
            get
            {
                HashDigest<SHA256>? blockHash = Store.IndexBlockHash(index);
                if (blockHash == null)
                {
                    throw new IndexOutOfRangeException();
                }

                return Blocks[blockHash.Value];
            }
        }

        public static void Validate(IEnumerable<Block<T>> blocks)
        {
            HashDigest<SHA256>? prevHash = null;
            DateTime? prevTimestamp = null;
            DateTime now = DateTime.UtcNow;
            IEnumerable<(ulong i, DifficultyExpectation)> indexedDifficulties =
                ExpectDifficulties(blocks)
                .Select((exp, i) => { return ((ulong)i, exp); });

            foreach (var (i, exp) in indexedDifficulties)
            {
                Trace.Assert(exp.Block != null);
                Block<T> block = exp.Block;

                if (i != block.Index)
                {
                    throw new InvalidBlockIndexException(
                        $"the expected block index is {i}, but its index is" +
                        $" {block.Index}'"
                    );
                }

                if (block.Difficulty < exp.Difficulty)
                {
                    throw new InvalidBlockDifficultyException(
                        $"the expected difficulty of the block #{i} " +
                        $"is {exp.Difficulty}, but its difficulty is " +
                        $"{block.Difficulty}'"
                    );
                }

                if (block.PreviousHash != prevHash)
                {
                    if (prevHash == null)
                    {
                        throw new InvalidBlockPreviousHashException(
                            "the genesis block must have not previous block"
                        );
                    }

                    throw new InvalidBlockPreviousHashException(
                        $"the block #{i} is not continuous from the " +
                        $"block #{i - 1}; while previous block's hash is " +
                        $"{prevHash}, the block #{i}'s pointer to " +
                        "the previous hash refers to " +
                        (block.PreviousHash?.ToString() ?? "nothing")
                    );
                }

                string fmtString = "MM/dd/yyyy hh:mm:ss.ffffff tt";

                if (now < block.Timestamp)
                {
                    throw new InvalidBlockTimestampException(
                        $"the block #{i}'s timestamp " +
                        $"({block.Timestamp.ToString(fmtString)}) is " +
                        $"later than now ({now.ToString(fmtString)})"
                    );
                }

                if (block.Timestamp <= prevTimestamp)
                {
                    throw new InvalidBlockTimestampException(
                        $"the block #{i}'s timestamp " +
                        $"({block.Timestamp.ToString(fmtString)}) is " +
                        $"earlier than the block #{i - 1}'s " +
                        $"({prevTimestamp.Value.ToString(fmtString)})"
                    );
                }

                block.Validate();
                prevHash = block.Hash;
                prevTimestamp = block.Timestamp;
            }
        }

        public IEnumerator<Block<T>> GetEnumerator()
        {
            foreach (HashDigest<SHA256> hash in Store.IterateIndex())
            {
                yield return Blocks[hash];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public AddressStateMap GetStates(
            IEnumerable<Address> addresses, HashDigest<SHA256>? offset = null)
        {
            if (offset == null)
            {
                offset = Store.IndexBlockHash(-1);
            }

            var states = new AddressStateMap();
            while (offset != null)
            {
                states = (AddressStateMap)states.SetItems(
                    Store.GetBlockStates(offset.Value)
                    .Where(
                        kv => addresses.Contains(kv.Key) &&
                        !states.ContainsKey(kv.Key))
                );
                if (states.Keys.SequenceEqual(addresses))
                {
                    break;
                }

                offset = Blocks[offset.Value].PreviousHash;
            }

            return states;
        }

        public void Append(Block<T> block)
        {
            Validate(Enumerable.Append(this, block));
            Blocks[block.Hash] = block;
            EvaluateActions(block);

            long index = Store.AppendIndex(block.Hash);
            ISet<TxId> txIds = block.Transactions
                .Select(t => t.Id)
                .ToImmutableHashSet();

            Store.UnstageTransactionIds(txIds);
            foreach (Transaction<T> tx in block.Transactions)
            {
                Store.AppendAddressTransactionId(tx.Recipient, tx.Id);
            }
        }

        public void StageTransactions(ISet<Transaction<T>> txs)
        {
            foreach (Transaction<T> tx in txs)
            {
                Transactions[tx.Id] = tx;
            }

            Store.StageTransactionIds(
                txs.Select(tx => tx.Id).ToImmutableHashSet());
        }

        public Block<T> MineBlock(Address rewardBeneficiary)
        {
            ulong index = Store.CountIndex();
            uint difficulty = ExpectDifficulties(this, yieldNext: true)
                .Where(t => t.Block == null)
                .First()
                .Difficulty;

            Block<T> block = Block<T>.Mine(
                index: index,
                difficulty: difficulty,
                rewardBeneficiary: rewardBeneficiary,
                previousHash: Store.IndexBlockHash((long)index - 1),
                timestamp: DateTime.UtcNow,
                transactions: Store.IterateStagedTransactionIds()
                .Select(txId => Store.GetTransaction<T>(txId))
                .OfType<Transaction<T>>()
                .ToList()
            );
            Append(block);

            return block;
        }

        internal HashDigest<SHA256> FindBranchPoint(BlockLocator locator)
        {
            // Assume locator is sorted descending by height.
            foreach (HashDigest<SHA256> hash in locator)
            {
                if (Blocks.ContainsKey(hash))
                {
                    return Blocks[hash].Hash;
                }
            }

            return this[0].Hash;
        }

        internal IEnumerable<HashDigest<SHA256>> FindNextHashes(
            BlockLocator locator,
            HashDigest<SHA256>? stop = null,
            int count = 500)
        {
            HashDigest<SHA256>? Next(HashDigest<SHA256> hash)
            {
                long nextIndex = (long)Blocks[hash].Index + 1;
                return Store.IndexBlockHash(nextIndex);
            }

            HashDigest<SHA256>? tip = Store.IndexBlockHash(-1);
            HashDigest<SHA256>? currentHash = Next(FindBranchPoint(locator));

            while (currentHash != null && count > 0)
            {
                yield return currentHash.Value;

                if (currentHash == stop || currentHash == tip)
                {
                    break;
                }

                currentHash = Next(currentHash.Value);
                count--;
            }
        }

        private static IEnumerable<DifficultyExpectation> ExpectDifficulties(
            IEnumerable<Block<T>> blocks, bool yieldNext = false)
        {
            DateTime? prevTimestamp = null;
            DateTime? prevPrevTimestamp = null;
            IEnumerable<Block<T>> blocks_ = blocks.Cast<Block<T>>();

            if (yieldNext)
            {
                blocks_ = blocks_.Append(null);
            }

            // genesis block's difficulty is 0
            yield return new DifficultyExpectation
            {
                Difficulty = 0,
                Block = blocks_.First(),
            };

            uint difficulty = 1;
            prevTimestamp = blocks_.FirstOrDefault()?.Timestamp;

            foreach (Block<T> block in blocks_.Skip(1))
            {
                bool needMore =
                    prevPrevTimestamp != null &&
                    (
                    prevPrevTimestamp == null ||
                    prevTimestamp - prevPrevTimestamp < BlockInterval
                    );
                difficulty = Math.Max(
                    needMore ? difficulty + 1 : difficulty - 1,
                    1
                );
                yield return new DifficultyExpectation
                {
                    Difficulty = difficulty,
                    Block = block,
                };

                if (block != null)
                {
                    prevPrevTimestamp = prevTimestamp;
                    prevTimestamp = block.Timestamp;
                }
            }
        }

        private void EvaluateActions(Block<T> block)
        {
            HashDigest<SHA256>? prevHash = block.PreviousHash;
            var states = new AddressStateMap();

            int seed = BitConverter.ToInt32(block.Hash.ToByteArray(), 0);
            foreach (Transaction<T> tx in block.Transactions)
            {
                int txSeed = seed ^ BitConverter.ToInt32(tx.Signature, 0);
                foreach (T action in tx.Actions)
                {
                    IEnumerable<Address> requestedAddresses =
                        action.RequestStates(tx.Sender, tx.Recipient);
                    AddressStateMap requested = GetStates(
                        requestedAddresses.Except(states.Keys),
                        prevHash);
                    states = (AddressStateMap)requested.SetItems(states);
                    var prevState = new AddressStateMap(
                        requestedAddresses
                        .Where(states.ContainsKey)
                        .ToImmutableDictionary(a => a, a => states[a]));
                    var context = new ActionContext(
                        from: tx.Sender,
                        to: tx.Recipient,
                        previousStates: prevState,
                        randomSeed: unchecked(txSeed++)
                    );
                    AddressStateMap changes = action.Execute(context);
                    states = (AddressStateMap)states.SetItems(changes);
                }
            }

            Store.SetBlockStates(block.Hash, states);
        }

        private struct DifficultyExpectation
        {
            internal Block<T> Block;

            internal uint Difficulty;
        }
    }
}
