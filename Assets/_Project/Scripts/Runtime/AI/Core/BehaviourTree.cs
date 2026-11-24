using System;
using System.Collections.Generic;
using System.Text;

namespace LastShift.AI.Core
{
    public enum Status { Success, Failure, Running }

    /// <summary>
    /// A small, reactive behaviour tree.
    ///
    /// The whole tree is evaluated from the root on every tick, so a higher-priority branch
    /// takes over the moment its conditions become true (e.g. the dog sees the player while it
    /// is following the trail). When that happens, the action that was running is told to stop
    /// (<see cref="Node.Abort"/>), so actions can clean up (stop moving, reset timers).
    ///
    /// Nodes keep their last status and the tick they ran on, which lets the debug view print the
    /// active branch, e.g. "Root > Track > FollowTrail [Running]".
    /// </summary>
    public abstract class Node
    {
        public readonly string Name;
        public Status Last { get; private set; } = Status.Failure;
        public int LastTick { get; private set; } = -1;
        protected BehaviourTree Tree { get; private set; }

        protected Node(string name) { Name = name; }

        internal virtual void Attach(BehaviourTree tree) => Tree = tree;
        public virtual IReadOnlyList<Node> Children => Array.Empty<Node>();

        public Status Tick()
        {
            Last = OnTick();
            LastTick = Tree.TickCount;
            return Last;
        }

        protected abstract Status OnTick();

        /// <summary>Stop whatever is running in this subtree (a higher-priority branch took over).</summary>
        public virtual void Abort() { }

        public bool RanThisTick => Tree != null && LastTick == Tree.TickCount;
    }

    /// <summary>Tries children in priority order; the first that doesn't fail wins.</summary>
    public sealed class Selector : Node
    {
        readonly Node[] children;
        int running = -1;
        public Selector(string name, params Node[] children) : base(name) { this.children = children; }
        public override IReadOnlyList<Node> Children => children;
        internal override void Attach(BehaviourTree tree) { base.Attach(tree); foreach (var c in children) c.Attach(tree); }

        protected override Status OnTick()
        {
            for (int i = 0; i < children.Length; i++)
            {
                var s = children[i].Tick();
                if (s == Status.Failure) continue;
                if (running >= 0 && running != i) children[running].Abort();   // pre-empted (or finished)
                running = s == Status.Running ? i : -1;
                return s;
            }
            if (running >= 0) { children[running].Abort(); running = -1; }
            return Status.Failure;
        }

        public override void Abort() { if (running >= 0) { children[running].Abort(); running = -1; } }
    }

    /// <summary>Runs children in order; fails as soon as one fails (conditions are re-checked every tick).</summary>
    public sealed class Sequence : Node
    {
        readonly Node[] children;
        int running = -1;
        public Sequence(string name, params Node[] children) : base(name) { this.children = children; }
        public override IReadOnlyList<Node> Children => children;
        internal override void Attach(BehaviourTree tree) { base.Attach(tree); foreach (var c in children) c.Attach(tree); }

        protected override Status OnTick()
        {
            for (int i = 0; i < children.Length; i++)
            {
                var s = children[i].Tick();
                if (s == Status.Success) continue;
                if (running >= 0 && running != i) children[running].Abort();
                running = s == Status.Running ? i : -1;
                return s;
            }
            if (running >= 0) { children[running].Abort(); running = -1; }
            return Status.Success;
        }

        public override void Abort() { if (running >= 0) { children[running].Abort(); running = -1; } }
    }

    /// <summary>A yes/no question about the world (never Running).</summary>
    public sealed class Condition : Node
    {
        readonly Func<bool> test;
        public Condition(string name, Func<bool> test) : base(name) { this.test = test; }
        protected override Status OnTick() => test() ? Status.Success : Status.Failure;
    }

    /// <summary>
    /// A leaf that does something over time. Enter runs the first tick it starts, Exit when it
    /// finishes or is aborted.
    /// </summary>
    public sealed class ActionNode : Node
    {
        readonly Func<Status> tick;
        readonly Action enter, exit;
        bool active;

        public ActionNode(string name, Func<Status> tick, Action enter = null, Action exit = null) : base(name)
        { this.tick = tick; this.enter = enter; this.exit = exit; }

        protected override Status OnTick()
        {
            if (!active) { active = true; enter?.Invoke(); }
            var s = tick();
            if (s != Status.Running) { active = false; exit?.Invoke(); }
            return s;
        }

        public override void Abort() { if (active) { active = false; exit?.Invoke(); } }
    }

    /// <summary>Lets its child run at most once every <c>seconds</c> (fails while cooling down).</summary>
    public sealed class Cooldown : Node
    {
        readonly Node child; readonly float seconds; float readyAt = float.NegativeInfinity;
        public Cooldown(string name, float seconds, Node child) : base(name) { this.child = child; this.seconds = seconds; }
        public override IReadOnlyList<Node> Children => new[] { child };
        internal override void Attach(BehaviourTree tree) { base.Attach(tree); child.Attach(tree); }

        protected override Status OnTick()
        {
            if (Tree.Now < readyAt) return Status.Failure;
            var s = child.Tick();
            if (s == Status.Success) readyAt = Tree.Now + seconds;
            return s;
        }

        public override void Abort() => child.Abort();
    }

    /// <summary>Always succeeds (runs the child for its side effect, e.g. an optional bark).</summary>
    public sealed class AlwaysSucceed : Node
    {
        readonly Node child;
        public AlwaysSucceed(string name, Node child) : base(name) { this.child = child; }
        public override IReadOnlyList<Node> Children => new[] { child };
        internal override void Attach(BehaviourTree tree) { base.Attach(tree); child.Attach(tree); }
        protected override Status OnTick() { child.Tick(); return Status.Success; }
        public override void Abort() => child.Abort();
    }

    /// <summary>The tree: owns the root, a clock and the tick counter.</summary>
    public sealed class BehaviourTree
    {
        public readonly Node Root;
        readonly Func<float> clock;
        public int TickCount { get; private set; }
        public float Now => clock();

        public BehaviourTree(Node root, Func<float> clock)
        {
            Root = root; this.clock = clock;
            root.Attach(this);
        }

        public Status Tick() { TickCount++; return Root.Tick(); }

        /// <summary>The branch that ran this tick and didn't fail, e.g. "Root > Track > FollowTrail [Running]".</summary>
        public string ActivePath()
        {
            var sb = new StringBuilder();
            Node n = Root, leaf = Root;
            while (n != null)
            {
                if (sb.Length > 0) sb.Append(" > ");
                sb.Append(n.Name);
                leaf = n;
                // Follow the deepest work: in a selector only one child got past Failure; in a sequence
                // the last child that ran is the one doing the work (earlier ones already succeeded)
                Node next = null;
                foreach (var c in n.Children)
                    if (c.RanThisTick && c.Last != Status.Failure && !(c is Condition)) next = c;
                n = next;
            }
            sb.Append(" [").Append(leaf.Last).Append(']');
            return sb.ToString();
        }
    }
}
