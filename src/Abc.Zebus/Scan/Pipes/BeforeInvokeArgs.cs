﻿namespace Abc.Zebus.Scan.Pipes
{
    public struct BeforeInvokeArgs
    {
        private readonly StateRef _stateRef;

        public BeforeInvokeArgs(PipeInvocation invocation) : this(invocation, new StateRef())
        {
        }

        public BeforeInvokeArgs(PipeInvocation invocation, StateRef stateRef)
        {
            _stateRef = stateRef;
            Invocation = invocation;
        }

        public readonly PipeInvocation Invocation;

        public object State
        {
            get { return _stateRef.Value; }
            set { _stateRef.Value = value; }
        }

        public class StateRef
        {
            public object Value;
        }
    }
}