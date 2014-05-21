﻿using System;
using Abc.Zebus.Util;

namespace Abc.Zebus.Dispatch
{
    public static class LocalDispatch
    {
        [ThreadStatic]
        private static bool _localDispatchDisabled;

        public static bool Enabled
        {
            get { return !_localDispatchDisabled; }
        }

        public static IDisposable Disable()
        {
            var currentValue = _localDispatchDisabled;
            _localDispatchDisabled = true;

            return new DisposableAction(() => _localDispatchDisabled = currentValue);
        }
    }
}