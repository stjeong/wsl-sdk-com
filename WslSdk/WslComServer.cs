﻿using System;
using System.Threading;
using WslSdk.Interop;

namespace WslSdk
{
    internal sealed class WslComServer
    {
        private WslComServer() { }

        private static WslComServer _instance = new WslComServer();

        public static WslComServer Instance
        {
            get { return _instance; }
        }

        // For thread-sync in lock
        private object syncRoot = new object();

        // Whether the server is running
        private bool _bRunning = false;

        // The ID of the thread that runs the message loop
        private int _nMainThreadID = 0;

        // The lock count (the number of active COM objects) in the server
        private int _nLockCnt = 0;

        // The timer to trigger GC every 5 seconds
        private Timer _gcTimer;

        private int _cookieWslService;

        /// <summary>
        /// The method is call every 5 seconds to GC the managed heap after 
        /// the COM server is started.
        /// </summary>
        /// <param name="stateInfo"></param>
        private static void GarbageCollect(object stateInfo)
        {
            // GC
            GC.Collect();
        }

        /// <summary>
        /// PreMessageLoop is responsible for registering the COM class 
        /// factories for the COM classes to be exposed from the server, and 
        /// initializing the key member variables of the COM server (e.g. 
        /// _nMainThreadID and _nLockCnt).
        /// </summary>
        private void PreMessageLoop()
        {
            //
            // Register the COM class factories.
            // 
            Guid clsidWslService = HelperMethods.GetGuidFromType(typeof(WslService));

            // Register the WslService class object
            int hResult = NativeMethods.CoRegisterClassObject(
                ref clsidWslService,                 // CLSID to be registered
                new WslServiceClassFactory(),     // Class factory
                NativeMethods.CLSCTX.LOCAL_SERVER,  // Context to run
                NativeMethods.REGCLS.MULTIPLEUSE | NativeMethods.REGCLS.SUSPENDED,
                out _cookieWslService);

            if (hResult != 0)
            {
                throw new ApplicationException(
                    "CoRegisterClassObject failed w/err 0x" + hResult.ToString("X"));
            }

            // Register other class objects 
            // ...

            // Inform the SCM about all the registered classes, and begins 
            // letting activation requests into the server process.
            hResult = NativeMethods.CoResumeClassObjects();

            if (hResult != 0)
            {
                // Revoke the registration of WslService on failure
                if (_cookieWslService != 0)
                    NativeMethods.CoRevokeClassObject(_cookieWslService);

                // Revoke the registration of other classes
                // ...

                throw new ApplicationException("CoResumeClassObjects failed w/err 0x" + hResult.ToString("X"));
            }

            //
            // Initialize member variables.
            // 

            // Records the ID of the thread that runs the COM server so that 
            // the server knows where to post the WM_QUIT message to exit the 
            // message loop.
            _nMainThreadID = NativeMethods.GetCurrentThreadId();

            // Records the count of the active COM objects in the server. 
            // When _nLockCnt drops to zero, the server can be shut down.
            _nLockCnt = 0;

            // Start the GC timer to trigger GC every 5 seconds.
            _gcTimer = new Timer(new TimerCallback(GarbageCollect), null, 5000, 5000);
        }

        /// <summary>
        /// RunMessageLoop runs the standard message loop. The message loop 
        /// quits when it receives the WM_QUIT message.
        /// </summary>
        private void RunMessageLoop()
        {
            NativeMethods.NativeMessage msg;
            while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }

        /// <summary>
        /// PostMessageLoop is called to revoke the registration of the COM 
        /// classes exposed from the server, and perform the cleanups.
        /// </summary>
        private void PostMessageLoop()
        {
            // 
            // Revoke the registration of the COM classes.
            // 

            // Revoke the registration of WslService
            if (_cookieWslService != 0)
            {
                NativeMethods.CoRevokeClassObject(_cookieWslService);
            }

            // Revoke the registration of other classes
            // ...

            //
            // Perform the cleanup.
            // 

            // Dispose the GC timer.
            if (_gcTimer != null)
            {
                _gcTimer.Dispose();
            }

            // Wait for any threads to finish.
            Thread.Sleep(1000);
        }

        /// <summary>
        /// Run the COM server. If the server is running, the function 
        /// returns directly.
        /// </summary>
        /// <remarks>The method is thread-safe.</remarks>
        public void Run()
        {
            lock (syncRoot) // Ensure thread-safe
            {
                // If the server is running, return directly.
                if (_bRunning)
                    return;

                // Indicate that the server is running now.
                _bRunning = true;
            }

            try
            {
                // Call PreMessageLoop to initialize the member variables 
                // and register the class factories.
                PreMessageLoop();

                try
                {
                    // Run the message loop.
                    RunMessageLoop();
                }
                finally
                {
                    // Call PostMessageLoop to revoke the registration.
                    PostMessageLoop();
                }
            }
            finally
            {
                _bRunning = false;
            }
        }

        /// <summary>
        /// Increase the lock count
        /// </summary>
        /// <returns>The new lock count after the increment</returns>
        /// <remarks>The method is thread-safe.</remarks>
        public int Lock()
        {
            return Interlocked.Increment(ref _nLockCnt);
        }

        /// <summary>
        /// Decrease the lock count. When the lock count drops to zero, post 
        /// the WM_QUIT message to the message loop in the main thread to 
        /// shut down the COM server.
        /// </summary>
        /// <returns>The new lock count after the increment</returns>
        public int Unlock()
        {
            int nRet = Interlocked.Decrement(ref _nLockCnt);

            // If lock drops to zero, attempt to terminate the server.
            if (nRet == 0)
            {
                // Post the WM_QUIT message to the main thread
                NativeMethods.PostThreadMessage(_nMainThreadID,
                    NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            return nRet;
        }

        /// <summary>
        /// Get the current lock count.
        /// </summary>
        /// <returns></returns>
        public int GetLockCount()
        {
            return _nLockCnt;
        }
    }
}
