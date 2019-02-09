using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Libplanet.Action;
using Libplanet.Blocks;
using Libplanet.Crypto;
using Libplanet.Tests.Common;
using Libplanet.Tests.Common.Action;
using Libplanet.Tests.Store;
using Libplanet.Tx;
using Xunit;

namespace Libplanet.Tests
{
    public class BlockchainTest : IDisposable
    {
        private FileStoreFixture _fx;
        private Blockchain<BaseAction> _blockchain;

        public BlockchainTest()
        {
            _fx = new FileStoreFixture();
            _blockchain = new Blockchain<BaseAction>(_fx.Store);
        }

        public void Dispose()
        {
            _fx.Dispose();
        }

        [Fact]
        public void CanMineBlock()
        {
            Block<BaseAction> block = _blockchain.MineBlock(_fx.Address1);
            block.Validate();
            Assert.Contains(block, _blockchain);

            Block<BaseAction> anotherBlock = _blockchain.MineBlock(_fx.Address2);
            anotherBlock.Validate();
            Assert.Contains(anotherBlock, _blockchain);
        }

        [Fact]
        public void CanFindBlockByIndex()
        {
            // use assignment to snooze compiler error (CS0201)
            Assert.Throws<IndexOutOfRangeException>(() => { var x = _blockchain[0]; });
            Block<BaseAction> block = _blockchain.MineBlock(_fx.Address1);
            Assert.Equal(block, _blockchain[0]);

            // To avoid InvalidBlockTimestampException.
            Thread.Sleep(100);

            Block<BaseAction> anotherBlock = _blockchain.MineBlock(_fx.Address2);
            Assert.Equal(anotherBlock, _blockchain[1]);
        }

        [Fact]
        public void CanIterate()
        {
            Assert.Empty(_blockchain);
            _blockchain.MineBlock(_fx.Address1);

            Assert.NotEmpty(_blockchain);
        }

        [Fact]
        public void CanStateTransactions()
        {
            Assert.Empty(_blockchain.Transactions);
            var txs = new HashSet<Transaction<BaseAction>>()
            {
                _fx.Transaction1,
                _fx.Transaction2,
            };
            _blockchain.StageTransactions(txs);
            Assert.Equal(
                txs,
                _blockchain.Transactions.Values.ToHashSet()
            );
        }

        [Fact]
        public void CanProcessActions()
        {
            var actions1 = new List<BaseAction>()
            {
                new Attack()
                {
                    Weapon = "sword",
                    Target = "goblin",
                },
                new Attack()
                {
                    Weapon = "sword",
                    Target = "orc",
                },
                new Attack()
                {
                    Weapon = "staff",
                    Target = "goblin",
                },
            };
            Transaction<BaseAction> tx1 = Transaction<BaseAction>.Make(
                new PrivateKey(),
                _fx.Address1,
                actions1,
                DateTime.UtcNow
            );

            _blockchain.StageTransactions(new HashSet<Transaction<BaseAction>> { tx1 });
            _blockchain.MineBlock(_fx.Address1);

            AddressStateMap states = _blockchain.GetStates(new List<Address> { _fx.Address1 });
            Assert.NotEmpty(states);

            var result = (BattleResult)states[_fx.Address1];
            Assert.Contains("sword", result.UsedWeapons);
            Assert.Contains("staff", result.UsedWeapons);
            Assert.Contains("orc", result.Targets);
            Assert.Contains("goblin", result.Targets);

            var actions2 = new List<BaseAction>()
            {
                new Attack()
                {
                    Weapon = "bow",
                    Target = "goblin",
                },
            };
            Transaction<BaseAction> tx2 = Transaction<BaseAction>.Make(
                new PrivateKey(),
                _fx.Address1,
                actions2,
                DateTime.UtcNow
            );

            _blockchain.StageTransactions(new HashSet<Transaction<BaseAction>> { tx2 });
            _blockchain.MineBlock(_fx.Address1);

            states = _blockchain.GetStates(new List<Address> { _fx.Address1 });
            result = (BattleResult)states[_fx.Address1];
            Assert.Contains("bow", result.UsedWeapons);
        }

        [Fact]
        public void CanAppendBlock()
        {
            Assert.Empty(_blockchain.Blocks);
            _blockchain.Append(_fx.Block1);
            Assert.Contains(_fx.Block1, _blockchain);
        }

        [Fact]
        public void CanStoreAddresses()
        {
            Assert.Empty(_blockchain.Addresses);
            var txs = new HashSet<Transaction<BaseAction>>()
            {
                _fx.Transaction1,
                _fx.Transaction2,
            };
            _blockchain.StageTransactions(txs);
            _blockchain.MineBlock(_fx.Address1);

            Assert.NotEmpty(_blockchain.Addresses);
            Assert.Contains(_fx.Transaction1, _blockchain.Addresses[_fx.Transaction1.Recipient]);
            Assert.DoesNotContain(_fx.Transaction2, _blockchain.Addresses[_fx.Transaction1.Recipient]);
        }

        [Fact]
        public void CanFindNextHashes()
        {
            DateTime datetime1 = DateTime.UtcNow;
            DateTime datetime2 = DateTime.UtcNow;

            if (datetime1 == datetime2)
            {
                string fmtString = "MM/dd/yyyy hh:mm:ss.ffffff tt";
                throw new Exception(
                    $"({datetime1.ToString(fmtString)}) is same with " +
                    $"({datetime2.ToString(fmtString)}) " +
                    $"tick 1: {datetime1.Ticks}" +
                    $"tick 2: {datetime2.Ticks}"
                );
            }

            _blockchain.Append(_fx.Block1);
            var block0 = _fx.Block1;
            var block1 = _blockchain.MineBlock(_fx.Address1);
            var block2 = _blockchain.MineBlock(_fx.Address1);
            var block3 = _blockchain.MineBlock(_fx.Address1);

            Assert.Equal(
                new[] { block1.Hash, block2.Hash, block3.Hash, },
                _blockchain.FindNextHashes(
                    new BlockLocator(new[] { block0.Hash })));
            Assert.Equal(
                new[] { block2.Hash, block3.Hash },
                _blockchain.FindNextHashes(
                    new BlockLocator(new[] { block1.Hash, block0.Hash })));
            Assert.Equal(
                new[] { block1.Hash, block2.Hash },
                _blockchain.FindNextHashes(
                    new BlockLocator(new[] { block0.Hash }),
                    stop: block2.Hash));
            Assert.Equal(
                new[] { block1.Hash, block2.Hash },
                _blockchain.FindNextHashes(
                    new BlockLocator(new[] { block0.Hash }),
                    count: 2));
        }
    }
}
