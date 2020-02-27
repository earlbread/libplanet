using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Bencodex.Types;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blocks;
using Libplanet.Crypto;
using Libplanet.Store;
using Libplanet.Tests.Blockchain;
using Libplanet.Tests.Common.Action;
using Libplanet.Tx;
using Xunit;
using Xunit.Abstractions;

namespace Libplanet.Tests.Store
{
    public abstract class StoreTest
    {
        protected ITestOutputHelper TestOutputHelper { get; set; }

        protected StoreFixture Fx { get; set; }

        protected Func<StoreFixture> FxConstructor { get; set; }

        [SkippableFact]
        public void ListChainId()
        {
            Assert.Empty(Fx.Store.ListChainIds());

            Fx.Store.PutBlock(Fx.Block1);
            Fx.Store.AppendIndex(Fx.StoreChainId, Fx.Block1.Hash);
            Assert.Equal(
                new[] { Fx.StoreChainId }.ToImmutableHashSet(),
                Fx.Store.ListChainIds().ToImmutableHashSet()
            );

            Guid arbitraryGuid = Guid.NewGuid();
            Fx.Store.AppendIndex(arbitraryGuid, Fx.Block1.Hash);
            Assert.Equal(
                new[] { Fx.StoreChainId, arbitraryGuid }.ToImmutableHashSet(),
                Fx.Store.ListChainIds().ToImmutableHashSet()
            );
        }

        [SkippableFact]
        public void DeleteChainId()
        {
            Block<DumbAction> block1 = TestUtils.MineNext(
                TestUtils.MineGenesis<DumbAction>(),
                new[] { Fx.Transaction1 });
            Fx.Store.AppendIndex(Fx.StoreChainId, block1.Hash);
            Guid arbitraryChainId = Guid.NewGuid();
            Fx.Store.AppendIndex(arbitraryChainId, block1.Hash);
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                new[] { "foo", "bar", "baz" }.ToImmutableHashSet(),
                block1.Hash,
                block1.Index
            );
            Fx.Store.IncreaseTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer);

            Fx.Store.DeleteChainId(Fx.StoreChainId);

            Assert.Equal(
                new[] { arbitraryChainId }.ToImmutableHashSet(),
                Fx.Store.ListChainIds().ToImmutableHashSet()
            );
            Assert.Empty(Fx.Store.ListStateKeys(Fx.StoreChainId).ToArray());
            Assert.Equal(0, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer));
        }

        [SkippableFact]
        public void CanonicalChainId()
        {
            Assert.Null(Fx.Store.GetCanonicalChainId());
            Guid a = Guid.NewGuid();
            Fx.Store.SetCanonicalChainId(a);
            Assert.Equal(a, Fx.Store.GetCanonicalChainId());
            Guid b = Guid.NewGuid();
            Fx.Store.SetCanonicalChainId(b);
            Assert.Equal(b, Fx.Store.GetCanonicalChainId());
        }

        [SkippableFact]
        public void ListAddresses()
        {
            Assert.Empty(Fx.Store.ListStateKeys(Fx.StoreChainId).ToArray());

            string[] stateKeys = { "a", "b", "c", "d", "e", "f", "g", "h" };
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                stateKeys.Take(3).ToImmutableHashSet(),
                Fx.Block1.Hash,
                Fx.Block1.Index
            );
            Assert.Equal(
                stateKeys.Take(3).ToImmutableHashSet(),
                Fx.Store.ListStateKeys(Fx.StoreChainId).ToImmutableHashSet()
            );
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                stateKeys.Skip(2).Take(3).ToImmutableHashSet(),
                Fx.Block2.Hash,
                Fx.Block2.Index
            );
            Assert.Equal(
                stateKeys.Take(5).ToImmutableHashSet(),
                Fx.Store.ListStateKeys(Fx.StoreChainId).ToImmutableHashSet()
            );
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                stateKeys.Skip(5).Take(3).ToImmutableHashSet(),
                Fx.Block3.Hash,
                Fx.Block3.Index
            );
            Assert.Equal(
                stateKeys.ToImmutableHashSet(),
                Fx.Store.ListStateKeys(Fx.StoreChainId).ToImmutableHashSet()
            );
        }

        [SkippableFact]
        public void ListAllStateReferences()
        {
            Address address1 = Fx.Address1;
            Address address2 = Fx.Address2;
            Address address3 = Fx.Address3;
            string stateKey1 = address1.ToHex().ToLowerInvariant();
            string stateKey2 = address2.ToHex().ToLowerInvariant();
            string stateKey3 = address3.ToHex().ToLowerInvariant();

            var store = Fx.Store;
            var chain = TestUtils.MakeBlockChain(new NullPolicy<DumbAction>(), store);

            var block1 = TestUtils.MineNext(chain.Genesis);
            var block2 = TestUtils.MineNext(block1);
            var block3 = TestUtils.MineNext(block2);

            Transaction<DumbAction> tx4 = Fx.MakeTransaction(
                new[]
                {
                    new DumbAction(address1, "foo1"),
                    new DumbAction(address2, "foo2"),
                }
            );
            Block<DumbAction> block4 = TestUtils.MineNext(
                block3,
                new[] { tx4 },
                blockInterval: TimeSpan.FromSeconds(10));

            Transaction<DumbAction> tx5 = Fx.MakeTransaction(
                new[]
                {
                    new DumbAction(address1, "bar1"),
                    new DumbAction(address3, "bar3"),
                }
            );
            Block<DumbAction> block5 = TestUtils.MineNext(
                block4,
                new[] { tx5 },
                blockInterval: TimeSpan.FromSeconds(10));

            Block<DumbAction> block6 = TestUtils.MineNext(
                block5,
                blockInterval: TimeSpan.FromSeconds(10));

            chain.Append(block1);
            chain.Append(block2);
            chain.Append(block3);
            chain.Append(block4);
            chain.Append(block5);
            chain.Append(block6);

            Guid chainId = chain.Id;
            IImmutableDictionary<string, IImmutableList<HashDigest<SHA256>>> refs;

            refs = store.ListAllStateReferences(chainId);
            Assert.Equal(
                new HashSet<string> { stateKey1, stateKey2, stateKey3 },
                refs.Keys.ToHashSet()
            );
            Assert.Equal(new[] { block4.Hash, block5.Hash }, refs[stateKey1]);
            Assert.Equal(new[] { block4.Hash }, refs[stateKey2]);
            Assert.Equal(new[] { block5.Hash }, refs[stateKey3]);

            refs = store.ListAllStateReferences(chainId, lowestIndex: block4.Index + 1);
            Assert.Equal(new HashSet<string> { stateKey1, stateKey3 }, refs.Keys.ToHashSet());
            Assert.Equal(new[] { block5.Hash }, refs[stateKey1]);
            Assert.Equal(new[] { block5.Hash }, refs[stateKey3]);

            refs = store.ListAllStateReferences(chainId, highestIndex: block4.Index);
            Assert.Equal(new HashSet<string> { stateKey1, stateKey2, }, refs.Keys.ToHashSet());
            Assert.Equal(new[] { block4.Hash }, refs[stateKey1]);
            Assert.Equal(new[] { block4.Hash }, refs[stateKey2]);
        }

        [SkippableFact]
        public void StoreBlock()
        {
            Assert.Empty(Fx.Store.IterateBlockHashes());
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block1.Hash));
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block3.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block1.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block3.Hash));
            Assert.False(Fx.Store.DeleteBlock(Fx.Block1.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block1.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block2.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block3.Hash));

            Fx.Store.PutBlock(Fx.Block1);
            Assert.Equal(1, Fx.Store.CountBlocks());
            Assert.Equal(
                new HashSet<HashDigest<SHA256>>
                {
                    Fx.Block1.Hash,
                },
                Fx.Store.IterateBlockHashes().ToHashSet());
            Assert.Equal(
                Fx.Block1,
                Fx.Store.GetBlock<DumbAction>(Fx.Block1.Hash));
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block3.Hash));
            Assert.Equal(Fx.Block1.Index, Fx.Store.GetBlockIndex(Fx.Block1.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block3.Hash));
            Assert.True(Fx.Store.ContainsBlock(Fx.Block1.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block2.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block3.Hash));

            Fx.Store.PutBlock(Fx.Block2);
            Assert.Equal(2, Fx.Store.CountBlocks());
            Assert.Equal(
                new HashSet<HashDigest<SHA256>>
                {
                    Fx.Block1.Hash,
                    Fx.Block2.Hash,
                },
                Fx.Store.IterateBlockHashes().ToHashSet());
            Assert.Equal(
                Fx.Block1,
                Fx.Store.GetBlock<DumbAction>(Fx.Block1.Hash));
            Assert.Equal(
                Fx.Block2,
                Fx.Store.GetBlock<DumbAction>(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block3.Hash));
            Assert.Equal(Fx.Block1.Index, Fx.Store.GetBlockIndex(Fx.Block1.Hash));
            Assert.Equal(Fx.Block2.Index, Fx.Store.GetBlockIndex(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block3.Hash));
            Assert.True(Fx.Store.ContainsBlock(Fx.Block1.Hash));
            Assert.True(Fx.Store.ContainsBlock(Fx.Block2.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block3.Hash));

            Assert.True(Fx.Store.DeleteBlock(Fx.Block1.Hash));
            Assert.Equal(1, Fx.Store.CountBlocks());
            Assert.Equal(
                new HashSet<HashDigest<SHA256>>
                {
                    Fx.Block2.Hash,
                },
                Fx.Store.IterateBlockHashes().ToHashSet());
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block1.Hash));
            Assert.Equal(
                Fx.Block2,
                Fx.Store.GetBlock<DumbAction>(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlock<DumbAction>(Fx.Block3.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block1.Hash));
            Assert.Equal(Fx.Block2.Index, Fx.Store.GetBlockIndex(Fx.Block2.Hash));
            Assert.Null(Fx.Store.GetBlockIndex(Fx.Block3.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block1.Hash));
            Assert.True(Fx.Store.ContainsBlock(Fx.Block2.Hash));
            Assert.False(Fx.Store.ContainsBlock(Fx.Block3.Hash));
        }

        [SkippableFact]
        public void StoreTx()
        {
            Assert.Equal(0, Fx.Store.CountTransactions());
            Assert.Empty(Fx.Store.IterateTransactionIds());
            Assert.Null(Fx.Store.GetTransaction<DumbAction>(Fx.Transaction1.Id));
            Assert.Null(Fx.Store.GetTransaction<DumbAction>(Fx.Transaction2.Id));
            Assert.False(Fx.Store.DeleteTransaction(Fx.Transaction1.Id));
            Assert.False(Fx.Store.ContainsTransaction(Fx.Transaction1.Id));
            Assert.False(Fx.Store.ContainsTransaction(Fx.Transaction2.Id));

            Fx.Store.PutTransaction(Fx.Transaction1);
            Assert.Equal(1, Fx.Store.CountTransactions());
            Assert.Equal(
                new HashSet<TxId>
                {
                    Fx.Transaction1.Id,
                },
                Fx.Store.IterateTransactionIds()
            );
            Assert.Equal(
                Fx.Transaction1,
                Fx.Store.GetTransaction<DumbAction>(Fx.Transaction1.Id)
            );
            Assert.Null(Fx.Store.GetTransaction<DumbAction>(Fx.Transaction2.Id));
            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction1.Id));
            Assert.False(Fx.Store.ContainsTransaction(Fx.Transaction2.Id));

            Fx.Store.PutTransaction(Fx.Transaction2);
            Assert.Equal(2, Fx.Store.CountTransactions());
            Assert.Equal(
                new HashSet<TxId>
                {
                    Fx.Transaction1.Id,
                    Fx.Transaction2.Id,
                },
                Fx.Store.IterateTransactionIds().ToHashSet()
            );
            Assert.Equal(
                Fx.Transaction1,
                Fx.Store.GetTransaction<DumbAction>(Fx.Transaction1.Id)
            );
            Assert.Equal(
                Fx.Transaction2,
                Fx.Store.GetTransaction<DumbAction>(Fx.Transaction2.Id));
            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction1.Id));
            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction2.Id));

            Assert.True(Fx.Store.DeleteTransaction(Fx.Transaction1.Id));
            Assert.Equal(1, Fx.Store.CountTransactions());
            Assert.Equal(
                new HashSet<TxId>
                {
                    Fx.Transaction2.Id,
                },
                Fx.Store.IterateTransactionIds()
            );
            Assert.Null(Fx.Store.GetTransaction<DumbAction>(Fx.Transaction1.Id));
            Assert.Equal(
                Fx.Transaction2,
                Fx.Store.GetTransaction<DumbAction>(Fx.Transaction2.Id)
            );
            Assert.False(Fx.Store.ContainsTransaction(Fx.Transaction1.Id));
            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction2.Id));
        }

        [SkippableFact]
        public void StoreIndex()
        {
            Assert.Equal(0, Fx.Store.CountIndex(Fx.StoreChainId));
            Assert.Empty(Fx.Store.IterateIndexes(Fx.StoreChainId));
            Assert.Null(Fx.Store.IndexBlockHash(Fx.StoreChainId, 0));
            Assert.Null(Fx.Store.IndexBlockHash(Fx.StoreChainId, -1));

            Assert.Equal(0, Fx.Store.AppendIndex(Fx.StoreChainId, Fx.Hash1));
            Assert.Equal(1, Fx.Store.CountIndex(Fx.StoreChainId));
            Assert.Equal(
                new List<HashDigest<SHA256>>()
                {
                    Fx.Hash1,
                },
                Fx.Store.IterateIndexes(Fx.StoreChainId));
            Assert.Equal(Fx.Hash1, Fx.Store.IndexBlockHash(Fx.StoreChainId, 0));
            Assert.Equal(Fx.Hash1, Fx.Store.IndexBlockHash(Fx.StoreChainId, -1));

            Assert.Equal(1, Fx.Store.AppendIndex(Fx.StoreChainId, Fx.Hash2));
            Assert.Equal(2, Fx.Store.CountIndex(Fx.StoreChainId));
            Assert.Equal(
                new List<HashDigest<SHA256>>()
                {
                    Fx.Hash1,
                    Fx.Hash2,
                },
                Fx.Store.IterateIndexes(Fx.StoreChainId));
            Assert.Equal(Fx.Hash1, Fx.Store.IndexBlockHash(Fx.StoreChainId, 0));
            Assert.Equal(Fx.Hash2, Fx.Store.IndexBlockHash(Fx.StoreChainId, 1));
            Assert.Equal(Fx.Hash2, Fx.Store.IndexBlockHash(Fx.StoreChainId, -1));
            Assert.Equal(Fx.Hash1, Fx.Store.IndexBlockHash(Fx.StoreChainId, -2));
        }

        [SkippableFact]
        public void IterateIndexes()
        {
            var ns = Fx.StoreChainId;
            var store = Fx.Store;

            store.AppendIndex(ns, Fx.Hash1);
            store.AppendIndex(ns, Fx.Hash2);
            store.AppendIndex(ns, Fx.Hash3);

            var indexes = store.IterateIndexes(ns).ToArray();
            Assert.Equal(new[] { Fx.Hash1, Fx.Hash2, Fx.Hash3 }, indexes);

            indexes = store.IterateIndexes(ns, 1).ToArray();
            Assert.Equal(new[] { Fx.Hash2, Fx.Hash3 }, indexes);

            indexes = store.IterateIndexes(ns, 2).ToArray();
            Assert.Equal(new[] { Fx.Hash3 }, indexes);

            indexes = store.IterateIndexes(ns, 3).ToArray();
            Assert.Equal(new HashDigest<SHA256>[] { }, indexes);

            indexes = store.IterateIndexes(ns, 4).ToArray();
            Assert.Equal(new HashDigest<SHA256>[] { }, indexes);

            indexes = store.IterateIndexes(ns, limit: 0).ToArray();
            Assert.Equal(new HashDigest<SHA256>[] { }, indexes);

            indexes = store.IterateIndexes(ns, limit: 1).ToArray();
            Assert.Equal(new[] { Fx.Hash1 }, indexes);

            indexes = store.IterateIndexes(ns, limit: 2).ToArray();
            Assert.Equal(new[] { Fx.Hash1, Fx.Hash2 }, indexes);

            indexes = store.IterateIndexes(ns, limit: 3).ToArray();
            Assert.Equal(new[] { Fx.Hash1, Fx.Hash2, Fx.Hash3 }, indexes);

            indexes = store.IterateIndexes(ns, limit: 4).ToArray();
            Assert.Equal(new[] { Fx.Hash1, Fx.Hash2, Fx.Hash3 }, indexes);

            indexes = store.IterateIndexes(ns, 1, 1).ToArray();
            Assert.Equal(new[] { Fx.Hash2 }, indexes);
        }

        [SkippableFact]
        public void LookupStateReference()
        {
            Address address = Fx.Address1;
            string stateKey = address.ToHex().ToLowerInvariant();

            Transaction<DumbAction> tx4 = Fx.MakeTransaction(
                new DumbAction[] { new DumbAction(address, "foo") }
            );
            Block<DumbAction> block4 = TestUtils.MineNext(Fx.Block3, new[] { tx4 });

            Transaction<DumbAction> tx5 = Fx.MakeTransaction(
                new DumbAction[] { new DumbAction(address, "bar") }
            );
            Block<DumbAction> block5 = TestUtils.MineNext(block4, new[] { tx5 });

            Block<DumbAction> block6 = TestUtils.MineNext(block5, new Transaction<DumbAction>[0]);

            Assert.Null(Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, Fx.Block3));
            Assert.Null(Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block4));
            Assert.Null(Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block5));
            Assert.Null(Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block6));

            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                tx4.UpdatedAddresses.Select(a => a.ToHex().ToLowerInvariant()).ToImmutableHashSet(),
                block4.Hash,
                block4.Index
            );
            Assert.Null(Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, Fx.Block3));
            Assert.Equal(
                Tuple.Create(block4.Hash, block4.Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block4)
            );
            Assert.Equal(
                Tuple.Create(block4.Hash, block4.Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block5)
            );
            Assert.Equal(
                Tuple.Create(block4.Hash, block4.Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block6)
            );

            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                tx5.UpdatedAddresses.Select(a => a.ToHex().ToLowerInvariant()).ToImmutableHashSet(),
                block5.Hash,
                block5.Index
            );
            Assert.Null(Fx.Store.LookupStateReference(
                Fx.StoreChainId, stateKey, Fx.Block3));
            Assert.Equal(
                Tuple.Create(block4.Hash, block4.Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block4)
            );
            Assert.Equal(
                Tuple.Create(block5.Hash, block5.Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block5)
            );
            Assert.Equal(
                Tuple.Create(block5.Hash, block5.Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey, block6)
            );
        }

        [SkippableFact]
        public void IterateStateReferences()
        {
            Address address = Fx.Address1;
            string stateKey = address.ToHex().ToLowerInvariant();
            var addresses = new[] { address }.ToImmutableHashSet();
            IImmutableSet<string> stateKeys = addresses
                .Select(a => a.ToHex().ToLowerInvariant())
                .ToImmutableHashSet();

            Block<DumbAction> block1 = Fx.Block1;
            Block<DumbAction> block2 = Fx.Block2;
            Block<DumbAction> block3 = Fx.Block3;

            Transaction<DumbAction> tx4 = Fx.MakeTransaction(
                new[] { new DumbAction(address, "foo") }
            );
            Block<DumbAction> block4 = TestUtils.MineNext(block3, new[] { tx4 });

            Transaction<DumbAction> tx5 = Fx.MakeTransaction(
                new[] { new DumbAction(address, "bar") }
            );
            Block<DumbAction> block5 = TestUtils.MineNext(block4, new[] { tx5 });

            Assert.Empty(Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey));

            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                stateKeys,
                block4.Hash,
                block4.Index
            );
            Assert.Equal(
                new[] { Tuple.Create(block4.Hash, block4.Index) },
                Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey)
            );

            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                stateKeys,
                block5.Hash,
                block5.Index
            );
            Assert.Equal(
                new[]
                {
                    Tuple.Create(block5.Hash, block5.Index),
                    Tuple.Create(block4.Hash, block4.Index),
                },
                Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey)
            );

            Fx.Store.StoreStateReference(Fx.StoreChainId, stateKeys, block3.Hash, block3.Index);
            Fx.Store.StoreStateReference(Fx.StoreChainId, stateKeys, block2.Hash, block2.Index);
            Fx.Store.StoreStateReference(Fx.StoreChainId, stateKeys, block1.Hash, block1.Index);

            Assert.Equal(
                new[]
                {
                    Tuple.Create(block5.Hash, block5.Index),
                    Tuple.Create(block4.Hash, block4.Index),
                    Tuple.Create(block3.Hash, block3.Index),
                    Tuple.Create(block2.Hash, block2.Index),
                    Tuple.Create(block1.Hash, block1.Index),
                },
                Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey)
            );

            Assert.Equal(
                new[]
                {
                    Tuple.Create(block5.Hash, block5.Index),
                    Tuple.Create(block4.Hash, block4.Index),
                },
                Fx.Store.IterateStateReferences(
                    Fx.StoreChainId,
                    stateKey,
                    lowestIndex: block4.Index
                )
            );

            Assert.Equal(
                new[]
                {
                    Tuple.Create(block2.Hash, block2.Index),
                    Tuple.Create(block1.Hash, block1.Index),
                },
                Fx.Store.IterateStateReferences(
                    Fx.StoreChainId, stateKey, highestIndex: block2.Index)
            );

            Assert.Equal(
                new[]
                {
                    Tuple.Create(block3.Hash, block3.Index),
                    Tuple.Create(block2.Hash, block2.Index),
                },
                Fx.Store.IterateStateReferences(
                    Fx.StoreChainId, stateKey, highestIndex: block3.Index, limit: 2)
            );

            Assert.Throws<ArgumentException>(() =>
            {
                Fx.Store.IterateStateReferences(
                    Fx.StoreChainId,
                    stateKey,
                    highestIndex: block2.Index,
                    lowestIndex: block3.Index);
            });
        }

        [SkippableFact]
        public void StoreStateReferenceAllowsDuplication()
        {
            const string stateKey1 = "foo", stateKey2 = "bar", stateKey3 = "baz";
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                new[] { stateKey1, stateKey2 }.ToImmutableHashSet(),
                Fx.Block1.Hash,
                Fx.Block1.Index
            );
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                new[] { stateKey2, stateKey3 }.ToImmutableHashSet(),
                Fx.Block1.Hash,
                Fx.Block1.Index
            );
            var expectedStateRefs = new[]
            {
                new Tuple<HashDigest<SHA256>, long>(Fx.Block1.Hash, Fx.Block1.Index),
            };
            Assert.Equal(
                expectedStateRefs,
                Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey1)
            );
            Assert.Equal(
                expectedStateRefs,
                Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey2)
            );
            Assert.Equal(
                expectedStateRefs,
                Fx.Store.IterateStateReferences(Fx.StoreChainId, stateKey3)
            );
        }

        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [SkippableTheory]
        public void ForkStateReferences(int branchPointIndex)
        {
            Address address1 = Fx.Address1;
            Address address2 = Fx.Address2;
            string stateKey1 = address1.ToHex().ToLowerInvariant();
            string stateKey2 = address2.ToHex().ToLowerInvariant();
            Block<DumbAction> prevBlock = Fx.Block3;
            Guid targetChainId = Guid.NewGuid();

            Transaction<DumbAction> tx1 = Fx.MakeTransaction(
                new List<DumbAction>(),
                new HashSet<Address> { address1 }.ToImmutableHashSet());

            Transaction<DumbAction> tx2 = Fx.MakeTransaction(
                new List<DumbAction>(),
                new HashSet<Address> { address2 }.ToImmutableHashSet());

            var txs1 = new[] { tx1 };
            var blocks = new List<Block<DumbAction>>
            {
                TestUtils.MineNext(prevBlock, txs1),
            };
            blocks.Add(TestUtils.MineNext(blocks[0], txs1));
            blocks.Add(TestUtils.MineNext(blocks[1], txs1));

            HashSet<Address> updatedAddresses;
            foreach (Block<DumbAction> block in blocks)
            {
                updatedAddresses = new HashSet<Address> { address1 };
                Fx.Store.StoreStateReference(
                    Fx.StoreChainId,
                    updatedAddresses.Select(a => a.ToHex().ToLowerInvariant()).ToImmutableHashSet(),
                    block.Hash,
                    block.Index
                );
            }

            var txs2 = new[] { tx2 };
            blocks.Add(TestUtils.MineNext(blocks[2], txs2));

            updatedAddresses = new HashSet<Address> { address2 };
            Fx.Store.StoreStateReference(
                Fx.StoreChainId,
                updatedAddresses.Select(a => a.ToHex().ToLowerInvariant()).ToImmutableHashSet(),
                blocks[3].Hash,
                blocks[3].Index
            );

            var branchPoint = blocks[branchPointIndex];
            Fx.Store.ForkStateReferences(
                Fx.StoreChainId,
                targetChainId,
                branchPoint);

            var actual = Fx.Store.LookupStateReference(
                Fx.StoreChainId,
                stateKey1,
                blocks[3]);

            Assert.Equal(
                Tuple.Create(blocks[2].Hash, blocks[2].Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey1, blocks[3]));
            Assert.Equal(
                Tuple.Create(blocks[3].Hash, blocks[3].Index),
                Fx.Store.LookupStateReference(Fx.StoreChainId, stateKey2, blocks[3]));
            Assert.Equal(
                    Tuple.Create(blocks[branchPointIndex].Hash, blocks[branchPointIndex].Index),
                    Fx.Store.LookupStateReference(targetChainId, stateKey1, blocks[3]));
            Assert.Null(
                    Fx.Store.LookupStateReference(targetChainId, stateKey2, blocks[3]));
        }

        [SkippableTheory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        // Check the count of state references after ForkStateReferences().
        public void ForkStateReferencesCheckCount(int branchPointIndex)
        {
            Block<DumbAction>[] blocks =
            {
                Fx.GenesisBlock, Fx.Block1, Fx.Block2, Fx.Block3,
            };
            string[] stateKeys = { "a", "b", "c", "d", };

            foreach (var (block, stateKey) in blocks.Zip(stateKeys, ValueTuple.Create))
            {
                Fx.Store.StoreStateReference(
                    Fx.StoreChainId,
                    ImmutableHashSet<string>.Empty.Add(stateKey),
                    block.Hash,
                    block.Index);
            }

            // Check there are as many state references in the chain as the expected count.
            Assert.Equal(4, Fx.Store.ListAllStateReferences(Fx.StoreChainId).Count);

            var forkedChainId = Guid.NewGuid();
            var branchPoint = blocks[branchPointIndex];
            Fx.Store.ForkStateReferences(Fx.StoreChainId, forkedChainId, branchPoint);

            // If the ForkStateReferences() worked correctly, there will be as many state references
            // in the forked chain as branchPointIndex + 1.
            Assert.Equal(
                branchPointIndex + 1,
                Fx.Store.ListAllStateReferences(forkedChainId).Count);
        }

        [SkippableFact]
        public void ForkStateReferencesChainIdNotFound()
        {
            var targetChainId = Guid.NewGuid();
            Address address = Fx.Address1;

            Assert.Throws<ChainIdNotFoundException>(() =>
                Fx.Store.ForkStateReferences(Fx.StoreChainId, targetChainId, Fx.Block1)
            );

            var chain = TestUtils.MakeBlockChain(new NullPolicy<DumbAction>(), Fx.Store);
            chain.Append(Fx.Block1);

            // Even if state references in a chain are empty it should not throw
            // ChainIdNotFoundException unless the chain in itself does not exist.
            Fx.Store.ForkStateReferences(chain.Id, targetChainId, Fx.Block1);
        }

        [SkippableFact]
        public void StoreStage()
        {
            Fx.Store.PutTransaction(Fx.Transaction1);
            Fx.Store.PutTransaction(Fx.Transaction2);
            Assert.Empty(Fx.Store.IterateStagedTransactionIds());

            var txIds = new HashSet<TxId>
            {
                Fx.Transaction1.Id,
                Fx.Transaction2.Id,
            }.ToImmutableHashSet();

            Fx.Store.StageTransactionIds(txIds);
            Assert.Equal(
                new HashSet<TxId>()
                {
                    Fx.Transaction1.Id,
                    Fx.Transaction2.Id,
                },
                Fx.Store.IterateStagedTransactionIds().ToHashSet());

            Fx.Store.UnstageTransactionIds(
                new HashSet<TxId>
                {
                    Fx.Transaction1.Id,
                });
            Assert.Equal(
                new HashSet<TxId>()
                {
                    Fx.Transaction2.Id,
                },
                Fx.Store.IterateStagedTransactionIds().ToHashSet());
        }

        [SkippableFact]
        public void StoreStageOnce()
        {
            Fx.Store.PutTransaction(Fx.Transaction1);
            Fx.Store.PutTransaction(Fx.Transaction2);

            var txIds = new HashSet<TxId>
            {
                Fx.Transaction1.Id,
                Fx.Transaction2.Id,
            }.ToImmutableHashSet();

            Fx.Store.StageTransactionIds(txIds);
            Fx.Store.StageTransactionIds(ImmutableHashSet<TxId>.Empty.Add(Fx.Transaction1.Id));

            Assert.Equal(
                new[] { Fx.Transaction1.Id, Fx.Transaction2.Id }.OrderBy(txId => txId.ToHex()),
                Fx.Store.IterateStagedTransactionIds().OrderBy(txId => txId.ToHex()));
        }

        [SkippableFact]
        public void BlockState()
        {
            Assert.Null(Fx.Store.GetBlockStates(Fx.Hash1));
            IImmutableDictionary<string, IValue> states = new Dictionary<string, IValue>()
            {
                ["foo"] = new Bencodex.Types.Dictionary(new Dictionary<IKey, IValue>
                {
                    { (Text)"a", (Integer)1 },
                }),
                ["bar"] = new Bencodex.Types.Dictionary(new Dictionary<IKey, IValue>
                {
                    { (Text)"b", (Integer)2 },
                }),
            }.ToImmutableDictionary();
            Fx.Store.SetBlockStates(Fx.Hash1, states);

            IImmutableDictionary<string, IValue> actual = Fx.Store.GetBlockStates(Fx.Hash1);
            Assert.Equal(states["foo"], actual["foo"]);
            Assert.Equal(states["bar"], actual["bar"]);
        }

        [SkippableFact]
        public void TxNonce()
        {
            Assert.Equal(0, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer));
            Assert.Equal(0, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction2.Signer));

            Fx.Store.IncreaseTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer);
            Assert.Equal(1, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer));
            Assert.Equal(0, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction2.Signer));
            Assert.Equal(
                new Dictionary<Address, long>
                {
                    [Fx.Transaction1.Signer] = 1,
                },
                Fx.Store.ListTxNonces(Fx.StoreChainId).ToDictionary(p => p.Key, p => p.Value)
            );

            Fx.Store.IncreaseTxNonce(Fx.StoreChainId, Fx.Transaction2.Signer, 5);
            Assert.Equal(1, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer));
            Assert.Equal(5, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction2.Signer));
            Assert.Equal(
                new Dictionary<Address, long>
                {
                    [Fx.Transaction1.Signer] = 1,
                    [Fx.Transaction2.Signer] = 5,
                },
                Fx.Store.ListTxNonces(Fx.StoreChainId).ToDictionary(p => p.Key, p => p.Value)
            );

            Fx.Store.IncreaseTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer, 2);
            Assert.Equal(3, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction1.Signer));
            Assert.Equal(5, Fx.Store.GetTxNonce(Fx.StoreChainId, Fx.Transaction2.Signer));
            Assert.Equal(
                new Dictionary<Address, long>
                {
                    [Fx.Transaction1.Signer] = 3,
                    [Fx.Transaction2.Signer] = 5,
                },
                Fx.Store.ListTxNonces(Fx.StoreChainId).ToDictionary(p => p.Key, p => p.Value)
            );
        }

        [SkippableFact]
        public void ListTxNonces()
        {
            var chainId1 = Guid.NewGuid();
            var chainId2 = Guid.NewGuid();

            Address address1 = Fx.Address1;
            Address address2 = Fx.Address2;

            Assert.Empty(Fx.Store.ListTxNonces(chainId1));
            Assert.Empty(Fx.Store.ListTxNonces(chainId2));

            Fx.Store.IncreaseTxNonce(chainId1, address1);
            Assert.Equal(
                new Dictionary<Address, long> { [address1] = 1, },
                Fx.Store.ListTxNonces(chainId1));

            Fx.Store.IncreaseTxNonce(chainId2, address2);
            Assert.Equal(
                new Dictionary<Address, long> { [address2] = 1, },
                Fx.Store.ListTxNonces(chainId2));

            Fx.Store.IncreaseTxNonce(chainId1, address1);
            Fx.Store.IncreaseTxNonce(chainId1, address2);
            Assert.Equal(
                new Dictionary<Address, long> { [address1] = 2, [address2] = 1, },
                Fx.Store.ListTxNonces(chainId1));

            Fx.Store.IncreaseTxNonce(chainId2, address1);
            Fx.Store.IncreaseTxNonce(chainId2, address2);
            Assert.Equal(
                new Dictionary<Address, long> { [address1] = 1, [address2] = 2, },
                Fx.Store.ListTxNonces(chainId2));
        }

        [SkippableFact]
        public void IndexBlockHashReturnNull()
        {
            Fx.Store.PutBlock(Fx.Block1);
            Fx.Store.AppendIndex(Fx.StoreChainId, Fx.Block1.Hash);
            Assert.Equal(1, Fx.Store.CountIndex(Fx.StoreChainId));
            Assert.Null(Fx.Store.IndexBlockHash(Fx.StoreChainId, 2));
        }

        [SkippableFact]
        public void ContainsBlockWithoutCache()
        {
            Fx.Store.PutBlock(Fx.Block1);
            Fx.Store.PutBlock(Fx.Block2);
            Fx.Store.PutBlock(Fx.Block3);

            Assert.True(Fx.Store.ContainsBlock(Fx.Block1.Hash));
            Assert.True(Fx.Store.ContainsBlock(Fx.Block2.Hash));
            Assert.True(Fx.Store.ContainsBlock(Fx.Block3.Hash));
        }

        [SkippableFact]
        public void ContainsTransactionWithoutCache()
        {
            Fx.Store.PutTransaction(Fx.Transaction1);
            Fx.Store.PutTransaction(Fx.Transaction2);
            Fx.Store.PutTransaction(Fx.Transaction3);

            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction1.Id));
            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction2.Id));
            Assert.True(Fx.Store.ContainsTransaction(Fx.Transaction3.Id));
        }

        [SkippableFact]
        public void TxAtomicity()
        {
            Transaction<AtomicityTestAction> MakeTx(
                System.Random random,
                MD5 md5,
                PrivateKey key,
                int txNonce
            )
            {
                byte[] arbitraryBytes = new byte[20];
                random.NextBytes(arbitraryBytes);
                byte[] digest = md5.ComputeHash(arbitraryBytes);
                var action = new AtomicityTestAction
                {
                    ArbitraryBytes = arbitraryBytes.ToImmutableArray(),
                    Md5Digest = digest.ToImmutableArray(),
                };
                return Transaction<AtomicityTestAction>.Create(
                    txNonce,
                    key,
                    new[] { action },
                    ImmutableHashSet<Address>.Empty,
                    DateTimeOffset.UtcNow
                );
            }

            const int taskCount = 5;
            const int txCount = 30;
            var md5Hasher = MD5.Create();
            Transaction<AtomicityTestAction> commonTx = MakeTx(
                new System.Random(),
                md5Hasher,
                new PrivateKey(),
                0
            );
            Task[] tasks = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                var task = new Task(() =>
                {
                    PrivateKey key = new PrivateKey();
                    var random = new System.Random();
                    var md5 = MD5.Create();
                    Transaction<AtomicityTestAction> tx;
                    for (int j = 0; j < 50; j++)
                    {
                        Fx.Store.PutTransaction(commonTx);
                    }

                    for (int j = 0; j < txCount; j++)
                    {
                        tx = MakeTx(random, md5, key, j + 1);
                        Fx.Store.PutTransaction(tx);
                    }
                });
                task.Start();
                tasks[i] = task;
            }

            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException e)
            {
                foreach (Exception innerException in e.InnerExceptions)
                {
                    TestOutputHelper.WriteLine(innerException.ToString());
                }

                throw;
            }

            Assert.Equal(1 + (taskCount * txCount), Fx.Store.CountTransactions());
            foreach (TxId txid in Fx.Store.IterateTransactionIds())
            {
                var tx = Fx.Store.GetTransaction<AtomicityTestAction>(txid);
                tx.Validate();
                Assert.Single(tx.Actions);
                AtomicityTestAction action = tx.Actions[0];
                Assert.Equal(
                    md5Hasher.ComputeHash(action.ArbitraryBytes.ToArray()),
                    action.Md5Digest.ToArray()
                );
            }
        }

        [SkippableFact]
        public void Copy()
        {
            using (StoreFixture fx = FxConstructor())
            using (StoreFixture fx2 = FxConstructor())
            {
                IStore s1 = fx.Store, s2 = fx2.Store;
                var blocks = new BlockChain<DumbAction>(
                    new NullPolicy<DumbAction>(),
                    s1,
                    Fx.GenesisBlock
                );

                // FIXME: Need to add more complex blocks/transactions.
                blocks.Append(Fx.Block1);
                blocks.Append(Fx.Block2);
                blocks.Append(Fx.Block3);

                s1.Copy(to: Fx.Store);
                Fx.Store.Copy(to: s2);

                Assert.Equal(s1.ListChainIds().ToHashSet(), s2.ListChainIds().ToHashSet());
                Assert.Equal(s1.GetCanonicalChainId(), s2.GetCanonicalChainId());
                foreach (Guid chainId in s1.ListChainIds())
                {
                    Assert.Equal(s1.IterateIndexes(chainId), s2.IterateIndexes(chainId));
                    foreach (HashDigest<SHA256> blockHash in s1.IterateIndexes(chainId))
                    {
                        Assert.Equal(
                            s1.GetBlock<DumbAction>(blockHash),
                            s2.GetBlock<DumbAction>(blockHash)
                        );
                        Assert.Equal(
                            s1.GetBlockStates(blockHash),
                            s2.GetBlockStates(blockHash)
                        );
                    }

                    Assert.Equal(
                        s1.ListAllStateReferences(chainId),
                        s2.ListAllStateReferences(chainId)
                    );
                }

                // ArgumentException is thrown if the destination store is not empty.
                Assert.Throws<ArgumentException>(() => Fx.Store.Copy(fx2.Store));
            }
        }

        private class AtomicityTestAction : IAction
        {
            public ImmutableArray<byte> ArbitraryBytes { get; set; }

            public ImmutableArray<byte> Md5Digest { get; set; }

            public IValue PlainValue =>
                new Bencodex.Types.Dictionary(new Dictionary<IKey, IValue>
                {
                    { (Text)"bytes", new Binary(ArbitraryBytes.ToArray()) },
                    { (Text)"md5", new Binary(Md5Digest.ToArray()) },
                });

            public void LoadPlainValue(IValue plainValue)
            {
                LoadPlainValue((Dictionary)plainValue);
            }

            public void LoadPlainValue(Dictionary plainValue)
            {
                ArbitraryBytes = plainValue.GetValue<Binary>("bytes").ToImmutableArray();
                Md5Digest = plainValue.GetValue<Binary>("md5").ToImmutableArray();
            }

            public IAccountStateDelta Execute(IActionContext context)
            {
                return context.PreviousStates;
            }

            public void Render(IActionContext context, IAccountStateDelta nextStates)
            {
            }

            public void Unrender(IActionContext context, IAccountStateDelta nextStates)
            {
            }
        }
    }
}
