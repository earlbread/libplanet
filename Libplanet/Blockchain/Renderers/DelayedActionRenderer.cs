#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Libplanet.Action;
using Libplanet.Blocks;
using Libplanet.Store;
using Serilog;

namespace Libplanet.Blockchain.Renderers
{
    /// <summary>
    /// An <see cref="IActionRenderer{T}"/> version of <see cref="DelayedRenderer{T}"/>.
    /// <para>Decorates an <see cref="IActionRenderer{T}"/> instance and delays the events until
    /// blocks are <em>confirmed</em> the certain number of blocks.  When blocks are recognized
    /// the delayed events relevant to these blocks are relayed to the decorated
    /// <see cref="IActionRenderer{T}"/>.</para>
    /// </summary>
    /// <typeparam name="T">An <see cref="IAction"/> type.  It should match to
    /// <see cref="BlockChain{T}"/>'s type parameter.</typeparam>
    /// <example>
    /// <code><![CDATA[
    /// IStore store = GetStore();
    /// IActionRenderer<ExampleAction> actionRenderer = new SomeActionRenderer();
    /// // Wraps the actionRenderer with DelayedActionRenderer; the SomeActionRenderer instance
    /// // becomes to receive event messages only after the relevent blocks are confirmed
    /// // by 3+ blocks.
    /// actionRenderer = new DelayedActionRenderer<ExampleAction>(
    ///    actionRenderer,
    ///    store,
    ///    confirmations: 3);
    /// // You must pass the same store to the BlockChain<T>() constructor:
    /// var chain = new BlockChain<ExampleAction>(
    ///    ...,
    ///    store: store,
    ///    renderers: new[] { actionRenderer });
    /// ]]></code>
    /// </example>
    public class DelayedActionRenderer<T> : IActionRenderer<T>
        where T : IAction, new()
    {
        private readonly ConcurrentDictionary<HashDigest<SHA256>, List<ActionEvaluation>>
            _bufferedActionRenders;

        private readonly ConcurrentDictionary<HashDigest<SHA256>, List<ActionEvaluation>>
            _bufferedActionUnrenders;

        private readonly AsyncLocal<Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>>
            _localRenderBuffer =
                new AsyncLocal<Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>>();

        private readonly AsyncLocal<Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>>
            _localUnrenderBuffer =
                new AsyncLocal<Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>>();

        private HashDigest<SHA256>? _eventReceivingBlock;
        private Reorg? _eventReceivingReorg;

        private ConcurrentDictionary<HashDigest<SHA256>, uint> _confirmed;
        private Block<T>? _tip;

        /// <summary>
        /// Creates a new <see cref="DelayedRenderer{T}"/> instance decorating the given
        /// <paramref name="renderer"/>.
        /// </summary>
        /// <param name="renderer">The renderer to decorate which has the <em>actual</em>
        /// implementations and receives delayed events.</param>
        /// <param name="store">The same store to what <see cref="BlockChain{T}"/> uses.</param>
        /// <param name="confirmations">The required number of confirmations to recognize a block.
        /// See also the <see cref="DelayedRenderer{T}.Confirmations"/> property.</param>
        public DelayedActionRenderer(IActionRenderer<T> renderer, IStore store, int confirmations)
        {
            if (confirmations == 0)
            {
                string msg =
                    "Zero confirmations mean nothing is delayed so that it is equivalent to the " +
                    $"bare {nameof(renderer)}; configure it to more than zero.";
                throw new ArgumentOutOfRangeException(nameof(confirmations), msg);
            }
            else if (confirmations < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(confirmations),
                    $"Expected more than zero {nameof(confirmations)}."
                );
            }

            Logger = Log.ForContext(GetType());
            Store = store;
            Confirmations = confirmations;
            _confirmed = new ConcurrentDictionary<HashDigest<SHA256>, uint>();
            ActionRenderer = renderer;
            _bufferedActionRenders =
                new ConcurrentDictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
            _bufferedActionUnrenders =
                new ConcurrentDictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
        }

        /// <summary>
        /// The same store to what <see cref="BlockChain{T}"/> uses.
        /// </summary>
        public IStore Store { get; }

        /// <summary>
        /// The required number of confirmations to recognize a block.
        /// <para>For example, the required confirmations are 2, the block #N is recognized after
        /// the block #N+1 and the block #N+2 are discovered.</para>
        /// </summary>
        public int Confirmations { get; }

        /// <summary>
        /// The <em>recognized</em> topmost block.  If not enough blocks are discovered yet,
        /// this property can be <c>null</c>.
        /// </summary>
        public Block<T>? Tip
        {
            get => _tip;
            private set
            {
                Block<T>? newTip = value;
                if (newTip is null || newTip.Equals(_tip))
                {
                    return;
                }

                if (_tip is null)
                {
                    Logger.Verbose(
                        $"{nameof(DelayedRenderer<T>)}.{nameof(Tip)} is tried to be updated to " +
                        "#{NewTipIndex} {NewTipHash} (from null).",
                        newTip.Index,
                        newTip.Hash
                    );
                }
                else
                {
                    Logger.Verbose(
                        $"{nameof(DelayedRenderer<T>)}.{nameof(Tip)} is tried to be updated to " +
                        "#{NewTipIndex} {NewTipHash} (from #{OldTipIndex} {OldTipHash}).",
                        newTip.Index,
                        newTip.Hash,
                        _tip.Index,
                        _tip.Hash
                    );
                }

                Block<T>? oldTip = _tip;
                _tip = newTip;
                if (oldTip is null)
                {
                    Logger.Debug(
                        $"{nameof(DelayedRenderer<T>)}.{nameof(Tip)} was updated to " +
                        "#{NewTipIndex} {NewTipHash} (from null).",
                        newTip.Index,
                        newTip.Hash
                    );
                }
                else
                {
                    Logger.Debug(
                        $"{nameof(DelayedRenderer<T>)}.{nameof(Tip)} was updated to " +
                        "#{NewTipIndex} {NewTipHash} (from #{OldTipIndex} {OldTipHash}).",
                        newTip.Index,
                        newTip.Hash,
                        oldTip.Index,
                        oldTip.Hash
                    );
                }

                if (oldTip is Block<T> oldTip_ && !oldTip.Equals(newTip))
                {
                    Block<T>? branchpoint = null;
                    if (!newTip.PreviousHash.Equals(oldTip_.Hash))
                    {
                        branchpoint = FindBranchpoint(oldTip, newTip);
                        if (branchpoint.Equals(oldTip) || branchpoint.Equals(newTip))
                        {
                            branchpoint = null;
                        }
                    }

                    OnTipChanged(oldTip, newTip, branchpoint);
                }
            }
        }

        /// <summary>
        /// The inner action renderer which has the <em>actual</em> implementations and receives
        /// delayed events.
        /// </summary>
        public IActionRenderer<T> ActionRenderer { get; }

        /// <summary>
        /// The logger to record internal state changes.
        /// </summary>
        protected ILogger Logger { get; }

        /// <inheritdoc cref="IRenderer{T}.RenderReorg(Block{T}, Block{T}, Block{T})"/>
        public void RenderReorg(Block<T> oldTip, Block<T> newTip, Block<T> branchpoint)
        {
            _confirmed.TryAdd(branchpoint.Hash, 0);
            _eventReceivingBlock = null;
            _eventReceivingReorg = new Reorg(
                LocateBlockPath(branchpoint, oldTip),
                LocateBlockPath(branchpoint, newTip),
                oldTip,
                newTip,
                branchpoint
            );
            _localUnrenderBuffer.Value =
                new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
        }

        /// <inheritdoc cref="DelayedRenderer{T}.RenderBlock(Block{T}, Block{T})"/>
        public void RenderBlock(Block<T> oldTip, Block<T> newTip)
        {
            _confirmed.TryAdd(oldTip.Hash, 0);

            if (_eventReceivingReorg is Reorg reorg &&
                reorg.OldTip.Equals(oldTip) &&
                reorg.NewTip.Equals(newTip))
            {
                if (_localUnrenderBuffer.Value
                    is Dictionary<HashDigest<SHA256>, List<ActionEvaluation>> buf)
                {
                    foreach (HashDigest<SHA256> block in reorg.Unrendered)
                    {
                        if (buf.TryGetValue(block, out List<ActionEvaluation>? b))
                        {
                            _bufferedActionUnrenders[block] = b;
                        }
                        else
                        {
                            _bufferedActionUnrenders.TryRemove(
                                block,
                                out List<ActionEvaluation>? removed
                            );
                            if (removed is List<ActionEvaluation> l)
                            {
                                Logger.Warning(
                                    "The existing {Count} buffered action unrenders for " +
                                    "the block {BlockHash} were overwritten.",
                                    l.Count,
                                    block
                                );
                            }
                        }

                        Logger.Debug(
                            "Committed {Count} buffered action unrenders from " +
                            "the block {BlockHash}.",
                            b?.Count ?? 0,
                            block
                        );
                    }

                    _localUnrenderBuffer.Value =
                        new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
                }
            }
            else
            {
                _eventReceivingReorg = null;
            }

            _eventReceivingBlock = newTip.Hash;
            _localRenderBuffer.Value = new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
        }

        /// <inheritdoc
        /// cref="IActionRenderer{T}.UnrenderAction(IAction, IActionContext, IAccountStateDelta)"/>
        public void UnrenderAction(
            IAction action,
            IActionContext context,
            IAccountStateDelta nextStates
        ) =>
            DelayUnrenderingAction(new ActionEvaluation(action, context, nextStates));

        /// <inheritdoc
        /// cref="IActionRenderer{T}.UnrenderActionError(IAction, IActionContext, Exception)"/>
        public void UnrenderActionError(IAction action, IActionContext context, Exception exception)
        {
            var eval = new ActionEvaluation(action, context, context.PreviousStates, exception);
            DelayUnrenderingAction(eval);
        }

        /// <inheritdoc
        /// cref="IActionRenderer{T}.RenderAction(IAction, IActionContext, IAccountStateDelta)"/>
        public void RenderAction(
            IAction action,
            IActionContext context,
            IAccountStateDelta nextStates
        ) =>
            DelayRenderingAction(new ActionEvaluation(action, context, nextStates));

        /// <inheritdoc
        /// cref="IActionRenderer{T}.RenderActionError(IAction, IActionContext, Exception)"/>
        public void RenderActionError(IAction action, IActionContext context, Exception exception)
        {
            var eval = new ActionEvaluation(action, context, context.PreviousStates, exception);
            DelayRenderingAction(eval);
        }

        /// <inheritdoc cref="IActionRenderer{T}.RenderBlockEnd(Block{T}, Block{T})"/>
        public void RenderBlockEnd(Block<T> oldTip, Block<T> newTip)
        {
            DiscoverBlock(newTip);
            Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>? buffer =
                _localRenderBuffer.Value;
            if (buffer is null)
            {
                return;
            }

            IEnumerable<HashDigest<SHA256>> rendered;
            if (_eventReceivingReorg is Reorg reorg)
            {
                rendered = reorg.Rendered;
            }
            else if (_eventReceivingBlock is HashDigest<SHA256> h)
            {
                rendered = new[] { h };
            }
            else
            {
                _localRenderBuffer.Value =
                    new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
                return;
            }

            foreach (HashDigest<SHA256> block in rendered)
            {
                if (buffer.TryGetValue(block, out List<ActionEvaluation>? b))
                {
                    _bufferedActionRenders[block] = b;
                }
                else
                {
                    _bufferedActionRenders.TryRemove(block, out List<ActionEvaluation>? removed);
                    if (removed is List<ActionEvaluation> l)
                    {
                        Logger.Warning(
                            "The existing {Count} buffered action renders for the block " +
                            "{BlockHash} were overwritten.",
                            l.Count,
                            block
                        );
                    }
                }

                Logger.Debug(
                    "Committed {Count} buffered action renders from the block {BlockHash}.",
                    b?.Count ?? 0,
                    block
                );
            }

            _localRenderBuffer.Value = new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>();
        }

        public void RenderReorgEnd(Block<T> oldTip, Block<T> newTip, Block<T> branchpoint)
        {
            _eventReceivingReorg = null;
        }

        /// <summary>
        /// Lists all descendants from <paramref name="lower"/> (exclusive) to
        /// <paramref name="upper"/> (inclusive).
        /// </summary>
        /// <param name="lower">The block to get its descendants (excluding it).</param>
        /// <param name="upper">The block to get its ancestors (including it).</param>
        /// <returns>Block hashes from <paramref name="lower"/> to <paramref name="upper"/>.
        /// Lower block hashes go first, and upper block hashes go last.
        /// Does not contain <paramref name="lower"/>'s hash but <paramref name="upper"/>'s one.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="upper"/> block's index
        /// is not greater than <paramref name="lower"/> block's index.</exception>
        internal ImmutableArray<HashDigest<SHA256>> LocateBlockPath(Block<T> lower, Block<T> upper)
        {
            if (lower.Index >= upper.Index)
            {
                throw new ArgumentException(
                    $"The {nameof(upper)} block (#{upper.Index} {upper}) must has the greater " +
                    $"index than the {nameof(lower)} block (#{lower.Index} {lower}).",
                    nameof(upper)
                );
            }

            IEnumerable<HashDigest<SHA256>> Iterate()
            {
                for (
                    Block<T>? b = upper;
                    b is Block<T> && b.Index > lower.Index;
                    b = b.PreviousHash is HashDigest<SHA256> prev ? Store.GetBlock<T>(prev) : null
                )
                {
                    yield return b.Hash;
                }
            }

            return Iterate().Reverse().ToImmutableArray();
        }

        /// <inheritdoc cref="DelayedRenderer{T}.OnTipChanged(Block{T}, Block{T}, Block{T}?)"/>
        protected void OnTipChanged(
            Block<T> oldTip,
            Block<T> newTip,
            Block<T>? branchpoint
        )
        {
            if (branchpoint is Block<T>)
            {
                ActionRenderer.RenderReorg(oldTip, newTip, branchpoint);
            }

            ActionRenderer.RenderBlock(oldTip, newTip);

            if (branchpoint is null)
            {
                RenderBufferedActionEvaluations(newTip.Hash, unrender: false);
            }
            else
            {
                var blocksToUnrender = LocateBlockPath(branchpoint, oldTip).Reverse();
                foreach (HashDigest<SHA256> hash in blocksToUnrender)
                {
                    RenderBufferedActionEvaluations(hash, unrender: true);
                }

                var blocksToRender = LocateBlockPath(branchpoint, newTip);
                foreach (HashDigest<SHA256> hash in blocksToRender)
                {
                    RenderBufferedActionEvaluations(hash, unrender: false);
                }
            }

            ActionRenderer.RenderBlockEnd(oldTip, newTip);

            if (branchpoint is Block<T>)
            {
                ActionRenderer.RenderReorgEnd(oldTip, newTip, branchpoint);
            }
        }

        private void DelayRenderingAction(ActionEvaluation eval)
        {
            long blockIndex = eval.InputContext.BlockIndex;
            HashDigest<SHA256> blockHash;
            if (_eventReceivingReorg is Reorg reorg)
            {
                blockHash = reorg.IndexHash(blockIndex, unrender: false);
            }
            else
            {
                if (_eventReceivingBlock is HashDigest<SHA256> h)
                {
                    blockHash = h;
                }
                else
                {
                    return;
                }
            }

            var buffer = _localRenderBuffer.Value ?? (_localRenderBuffer.Value =
                new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>());
            if (!buffer.TryGetValue(blockHash, out List<ActionEvaluation>? list))
            {
                buffer[blockHash] = list = new List<ActionEvaluation>();
            }

            list.Add(eval);
            Logger.Verbose(
                "Delayed an action render from #{BlockIndex} {BlockHash} (buffer: {Buffer}).",
                blockIndex,
                blockHash,
                list.Count
            );
        }

        private void DelayUnrenderingAction(ActionEvaluation eval)
        {
            if (_eventReceivingReorg is Reorg reorg)
            {
                var buffer = _localUnrenderBuffer.Value ?? (_localUnrenderBuffer.Value =
                    new Dictionary<HashDigest<SHA256>, List<ActionEvaluation>>());
                long blockIndex = eval.InputContext.BlockIndex;
                HashDigest<SHA256> blockHash = reorg.IndexHash(blockIndex, unrender: true);
                if (!buffer.TryGetValue(blockHash, out List<ActionEvaluation>? list))
                {
                    buffer[blockHash] = list = new List<ActionEvaluation>();
                }

                list.Add(eval);
                Logger.Verbose(
                    "Delayed an action unrender from #{BlockIndex} {BlockHash} (buffer: {Buffer}).",
                    blockIndex,
                    blockHash,
                    list.Count
                );
            }
            else
            {
                const string msg =
                    "An action unrender {@Action} from the block #{BlockIndex} was ignored due " +
                    "unexpected internal state.";
                Logger.Warning(msg, eval.Action, eval.InputContext.BlockIndex);
            }
        }

        private void RenderBufferedActionEvaluations(HashDigest<SHA256> blockHash, bool unrender)
        {
            ConcurrentDictionary<HashDigest<SHA256>, List<ActionEvaluation>> bufferMap
                = unrender
                ? _bufferedActionUnrenders
                : _bufferedActionRenders;
            string verb = unrender ? "unrender" : "render";
            if (bufferMap.TryGetValue(blockHash, out List<ActionEvaluation>? b) &&
                b is List<ActionEvaluation> buffer)
            {
                Logger.Debug(
                    $"Starts to {verb} {{BufferCount}} buffered actions from the block " +
                    "{BlockHash}...",
                    buffer.Count,
                    blockHash
                );
                RenderActionEvaluations(buffer, unrender);
                bufferMap.TryRemove(blockHash, out _);
            }
            else
            {
                Logger.Debug(
                    $"There are no buffered actions to {verb} for the block {{BlockHash}}.",
                    blockHash
                );
            }
        }

        private void RenderActionEvaluations(
            IEnumerable<ActionEvaluation> evaluations,
            bool unrender
        )
        {
            foreach (ActionEvaluation eval in evaluations)
            {
                if (eval.Exception is Exception e)
                {
                    if (unrender)
                    {
                        ActionRenderer.UnrenderActionError(eval.Action, eval.InputContext, e);
                    }
                    else
                    {
                        ActionRenderer.RenderActionError(eval.Action, eval.InputContext, e);
                    }
                }
                else
                {
                    if (unrender)
                    {
                        ActionRenderer.UnrenderAction(
                            eval.Action,
                            eval.InputContext,
                            eval.OutputStates
                        );
                    }
                    else
                    {
                        ActionRenderer.RenderAction(
                            eval.Action,
                            eval.InputContext,
                            eval.OutputStates
                        );
                    }
                }
            }
        }

        private void DiscoverBlock(Block<T> block)
        {
            if (_confirmed.ContainsKey(block.Hash))
            {
                return;
            }

            _confirmed.TryAdd(block.Hash, 0);

            var blocksToRender = new Stack<HashDigest<SHA256>>();
            blocksToRender.Push(block.Hash);

            HashDigest<SHA256>? prev = block.PreviousHash;
            do
            {
                if (!(prev is HashDigest<SHA256> prevHash &&
                      Store.GetBlock<T>(prevHash) is Block<T> prevBlock))
                {
                    break;
                }

                uint c = _confirmed.AddOrUpdate(prevHash, k => 1U, (k, v) => v + 1U);
                Logger.Verbose(
                    "The block #{BlockIndex} {BlockHash} has {Confirmations} confirmations. " +
                    "(The last confirmation was done by #{DiscoveredIndex} {DiscoveredHash}.)",
                    prevBlock.Index,
                    prevBlock.Hash,
                    c,
                    block.Index,
                    block.Hash
                );

                if (c >= Confirmations)
                {
                    if (!(Tip is Block<T> t))
                    {
                        Logger.Verbose(
                            "Promoting #{NewTipIndex} {NewTipHash} as a new tip since there is " +
                            "no tip yet...",
                            prevBlock.Index,
                            prevBlock.Hash
                        );
                        Tip = prevBlock;
                    }
                    else if (t.TotalDifficulty < prevBlock.TotalDifficulty)
                    {
                        Logger.Verbose(
                            "Promoting #{NewTipIndex} {NewTipHash} as a new tip since its total " +
                            "difficulty is more than the previous tip #{PreviousTipIndex} " +
                            "{PreviousTipHash} ({NewDifficulty} > {PreviousDifficulty}).",
                            prevBlock.Index,
                            prevBlock.Hash,
                            t.Index,
                            t.Hash,
                            prevBlock.TotalDifficulty,
                            t.TotalDifficulty
                        );
                        Tip = prevBlock;
                    }
                    else
                    {
                        Logger.Verbose(
                            "Although #{BlockIndex} {BlockHash} has been confirmed enough," +
                            "its difficulty is less than the current tip #{TipIndex} {TipHash} " +
                            "({Difficulty} < {TipDifficulty}).",
                            prevBlock.Index,
                            prevBlock.Hash,
                            t.Index,
                            t.Hash,
                            prevBlock.TotalDifficulty,
                            t.TotalDifficulty
                        );
                    }

                    break;
                }

                prev = prevBlock.PreviousHash;
            }
            while (true);
        }

        private Block<T> FindBranchpoint(Block<T> a, Block<T> b)
        {
            while (a is Block<T> && a.Index > b.Index && a.PreviousHash is HashDigest<SHA256> aPrev)
            {
                a = Store.GetBlock<T>(aPrev);
            }

            while (b is Block<T> && b.Index > a.Index && b.PreviousHash is HashDigest<SHA256> bPrev)
            {
                b = Store.GetBlock<T>(bPrev);
            }

            if (a is null || b is null || a.Index != b.Index)
            {
                throw new ArgumentException(
                    "Some previous blocks of two blocks are orphan.",
                    nameof(a)
                );
            }

            while (a.Index >= 0)
            {
                if (a.Equals(b))
                {
                    return a;
                }

                if (a.PreviousHash is HashDigest<SHA256> aPrev &&
                    b.PreviousHash is HashDigest<SHA256> bPrev)
                {
                    a = Store.GetBlock<T>(aPrev);
                    b = Store.GetBlock<T>(bPrev);
                    continue;
                }

                break;
            }

            throw new ArgumentException(
                "Two blocks do not have any ancestors in common.",
                nameof(a)
            );
        }

        private readonly struct Reorg
        {
            public readonly ImmutableArray<HashDigest<SHA256>> Unrendered;
            public readonly ImmutableArray<HashDigest<SHA256>> Rendered;
            public readonly Block<T> OldTip;
            public readonly Block<T> NewTip;
            public readonly Block<T> Branchpoint;

            public Reorg(
                ImmutableArray<HashDigest<SHA256>> unrendered,
                ImmutableArray<HashDigest<SHA256>> rendered,
                Block<T> oldTip,
                Block<T> newTip,
                Block<T> branchpoint
            )
            {
                Unrendered = unrendered;
                Rendered = rendered;
                OldTip = oldTip;
                NewTip = newTip;
                Branchpoint = branchpoint;
            }

            public HashDigest<SHA256> IndexHash(long index, bool unrender)
            {
                int offset = (int)(index - Branchpoint.Index);
                return (unrender ? Unrendered : Rendered)[offset - 1];
            }
        }
    }
}
