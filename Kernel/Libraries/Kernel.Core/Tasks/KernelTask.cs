﻿using System;
using Kernel.FOS_System;
using Kernel.FOS_System.Collections;
using Kernel.Core.Processes;
using Kernel.Hardware.Processes;
using Kernel.Hardware.VirtMem;

namespace Kernel.Core.Tasks
{
    public unsafe static class KernelTask
    {
        private class DeferredSyscallInfo : FOS_System.Object
        {
            public uint ProcessId;
            public uint ThreadId;
        }

        public static bool Terminating = false;
        private static Queue DeferredSyscallsInfo_Unqueued;
        private static Queue DeferredSyscallsInfo_Queued;

        private static Thread DeferredSyscallsThread;

        private static Pipes.Standard.StandardOutpoint StdOut;
        private static Pipes.Standard.StandardInpoint StdIn;

        private static int IRQ1HandlerId;

        public static void Main()
        {
            BasicConsole.WriteLine("Kernel task! ");
            BasicConsole.WriteLine(" > Executing normally...");

            DeferredSyscallsInfo_Unqueued = new Queue(256, false);
            DeferredSyscallsInfo_Queued = new Queue(DeferredSyscallsInfo_Unqueued.Capacity, false);
            for (int i = 0; i < DeferredSyscallsInfo_Unqueued.Capacity; i++)
            {
                DeferredSyscallsInfo_Unqueued.Push(new DeferredSyscallInfo());
            }

            try
            {
                BasicConsole.WriteLine(" > Initialising system calls...");
                Core.Processes.SystemCalls.Init();

                ProcessManager.CurrentProcess.SyscallHandler = SyscallHandler;
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.RegisterSyscallHandler);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.DeregisterSyscallHandler);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.StartThread);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.SleepThread);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.WakeThread);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.RegisterPipeOutpoint);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.GetNumPipeOutpoints);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.GetPipeOutpoints);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.WaitOnPipeCreate);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.ReadPipe);
                ProcessManager.CurrentProcess.SyscallsToHandle.Set((int)SystemCallNumbers.WritePipe);

                BasicConsole.WriteLine(" > Initialising IRQ handling...");
                IRQ1HandlerId = Hardware.Interrupts.Interrupts.AddIRQHandler(1, IRQ1, null, true, true, "KernelTask : IRQ1");

                //ProcessManager.CurrentProcess.OutputMemTrace = true;

                BasicConsole.WriteLine(" > Starting deferred syscalls thread...");
                DeferredSyscallsThread = ProcessManager.CurrentProcess.CreateThread(DeferredSyscallsThread_Main);

                BasicConsole.WriteLine(" > Starting GC Cleanup thread...");
                ProcessManager.CurrentProcess.CreateThread(Core.Tasks.GCCleanupTask.Main);

                BasicConsole.WriteLine(" > Starting Idle thread...");
                ProcessManager.CurrentProcess.CreateThread(Core.Tasks.IdleTask.Main);

                BasicConsole.WriteLine(" > Starting Window Manager...");
                Process WindowManagerProcess = ProcessManager.CreateProcess(WindowManagerTask.Main, "Window Manager", false, true);
                //WindowManagerProcess.OutputMemTrace = true;
                ProcessManager.RegisterProcess(WindowManagerProcess, Scheduler.Priority.Normal);

                BasicConsole.WriteLine(" > [Pausing 500ms]");
                Thread.Sleep(500);

                BasicConsole.WriteLine(" > Starting Device Manager...");
                Process DeviceManagerProcess = ProcessManager.CreateProcess(DeviceManagerTask.Main, "Device Manager", false, true);
                //DeviceManagerProcess.OutputMemTrace = true;
                ProcessManager.RegisterProcess(DeviceManagerProcess, Scheduler.Priority.Normal);
                

                //TODO: Main task for commands etc

                BasicConsole.WriteLine("Started.");

                BasicConsole.PrimaryOutputEnabled = false;
                BasicConsole.SecondaryOutputEnabled = false;

                try
                {
                    StdOut = new Pipes.Standard.StandardOutpoint(true);
                    int StdOutPipeId = StdOut.WaitForConnect();

                    StdIn = new Pipes.Standard.StandardInpoint(WindowManagerProcess.Id, false);
                    
                    uint loops = 0;
                    while (!Terminating)
                    {
                        try
                        {
                            StdOut.Write(StdOutPipeId, StdIn.Read(true), true);
                            //StdOut.Write(StdOutPipeId, "Kernel: Hello, processor! (" + (FOS_System.String)loops++ + ")\n");
                        }
                        catch
                        {
                            BasicConsole.WriteLine("KT > Error writing to StdOut!");
                            BasicConsole.WriteLine(ExceptionMethods.CurrentException.Message);
                        }

                        //SystemCallMethods.SleepThread(-1);
                    }
                }
                catch
                {
                    BasicConsole.WriteLine("KT > Error initialising!");
                    BasicConsole.WriteLine(ExceptionMethods.CurrentException.Message);
                }

                //BasicConsole.WriteLine(" > Starting Play Notes task...");
                //ProcessManager.CurrentProcess.CreateThread(Hardware.Tasks.PlayNotesTask.Main);

                //Core.Console.InitDefault();
                //Core.Shell.InitDefault();

                //BasicConsole.PrimaryOutputEnabled = false;
                //Core.Shell.Default.Execute();
                //BasicConsole.PrimaryOutputEnabled = true;

                //if (!Core.Shell.Default.Terminating)
                //{
                //    Core.Console.Default.WarningColour();
                //    Core.Console.Default.WriteLine("Abnormal shell shutdown!");
                //    Core.Console.Default.DefaultColour();
                //}
                //else
                //{
                //    Core.Console.Default.Clear();
                //}
            }
            catch
            {
                BasicConsole.PrimaryOutputEnabled = true;
                BasicConsole.SetTextColour(BasicConsole.warning_colour);
                BasicConsole.WriteLine(ExceptionMethods.CurrentException.Message);
                BasicConsole.SetTextColour(BasicConsole.default_colour);
            }

            BasicConsole.WriteLine();
            BasicConsole.WriteLine("---------------------");
            BasicConsole.WriteLine();
            BasicConsole.WriteLine("End of kernel task.");

            ExceptionMethods.HaltReason = "Managed main thread ended.";

            //TODO: Exit syscall
        }

        public static void DeferredSyscallsThread_Main()
        {
            while (!Terminating)
            {
                if (DeferredSyscallsInfo_Queued.Count == 0)
                {
                    Thread.Sleep_Indefinitely();
                }

                while (DeferredSyscallsInfo_Queued.Count > 0)
                {
                    // Scheduler must be disabled during pop/push from circular buffer or we can
                    //  end up in an infinite lock. Consider what happens if a process invokes 
                    //  a deferred system call during the pop/push here and at the end of this loop.
                    BasicConsole.WriteLine("DSC: Pausing scheduler...");
                    //Scheduler.Disable();
                    BasicConsole.WriteLine("DSC: Popping queued info object...");
                    DeferredSyscallInfo info = (DeferredSyscallInfo)DeferredSyscallsInfo_Queued.Pop();
                    BasicConsole.WriteLine("DSC: Resuming scheduler...");
                    //Scheduler.Enable();

                    BasicConsole.WriteLine("DSC: Getting process & thread...");
                    Process CallerProcess = ProcessManager.GetProcessById(info.ProcessId);
                    Thread CallerThread = ProcessManager.GetThreadById(info.ThreadId, CallerProcess);

                    BasicConsole.WriteLine("DSC: Getting data & calling...");
                    uint Return2 = CallerThread.Return2;
                    uint Return3 = CallerThread.Return3;
                    uint Return4 = CallerThread.Return4;
                    SystemCallResults result = HandleDeferredSystemCall(
                        CallerProcess, CallerThread,
                        (SystemCallNumbers)CallerThread.SysCallNumber, CallerThread.Param1, CallerThread.Param2, CallerThread.Param3,
                        ref Return2, ref Return3, ref Return4);

                    BasicConsole.WriteLine("DSC: Ending call...");
                    if (result != SystemCallResults.Deferred)
                    {
                        EndDeferredSystemCall(CallerThread, result, Return2, Return3, Return4);
                    }

                    BasicConsole.WriteLine("DSC: Resetting info object...");
                    info.ProcessId = 0;
                    info.ThreadId = 0;

                    // See comment at top of loop for why this is necessary
                    BasicConsole.WriteLine("DSC: Pausing scheduler...");
                    //Scheduler.Disable();
                    BasicConsole.WriteLine("DSC: Queuing info object...");
                    DeferredSyscallsInfo_Unqueued.Push(info);
                    BasicConsole.WriteLine("DSC: Resuming scheduler...");
                    //Scheduler.Enable();
                }
            }
        }
        public static unsafe SystemCallResults HandleDeferredSystemCall(
            Process CallerProcess, Thread CallerThread,
            SystemCallNumbers syscallNumber, uint Param1, uint Param2, uint Param3,
            ref uint Return2, ref uint Return3, ref uint Return4)
        {
            SystemCallResults result = SystemCallResults.Unhandled;

            switch (syscallNumber)
            {
                case SystemCallNumbers.StartThread:
                    BasicConsole.WriteLine("DSC: Start Thread");
                    Return2 = CallerProcess.CreateThread((ThreadStartMethod)Utilities.ObjectUtilities.GetObject((void*)Param1)).Id;
                    BasicConsole.WriteLine("DSC: Start Thread - done.");
                    result = SystemCallResults.OK;
                    break;
                case SystemCallNumbers.RegisterPipeOutpoint:
                    {
                        BasicConsole.WriteLine("DSC: Register Pipe Outpoint");
                        Pipes.PipeOutpoint outpoint;
                        bool registered = Pipes.PipeManager.RegisterPipeOutpoint(CallerProcess.Id, (Pipes.PipeClasses)Param1, (Pipes.PipeSubclasses)Param2, (int)Param3, out outpoint);
                        if (registered)
                        {
                            result = SystemCallResults.OK;
                        }
                        else
                        {
                            result = SystemCallResults.Fail;
                        }
                        BasicConsole.WriteLine("DSC: Register Pipe Outpoint - done.");
                    }
                    break;
                case SystemCallNumbers.GetNumPipeOutpoints:
                    {
                        BasicConsole.WriteLine("DSC: Get Num Pipe Outpoints");
                        int numOutpoints;
                        bool obtained = Pipes.PipeManager.GetNumPipeOutpoints((Pipes.PipeClasses)Param1, (Pipes.PipeSubclasses)Param2, out numOutpoints);
                        if (obtained)
                        {
                            result = SystemCallResults.OK;
                            Return2 = (uint)numOutpoints;
                        }
                        else
                        {
                            result = SystemCallResults.Fail;
                        }
                        BasicConsole.WriteLine("DSC: Get Num Pipe Outpoints - done");
                    }
                    break;
                case SystemCallNumbers.GetPipeOutpoints:
                    {
                        BasicConsole.WriteLine("DSC: Get Pipe Outpoints");
                        
                        bool obtained = Pipes.PipeManager.GetPipeOutpoints(CallerProcess, (Pipes.PipeClasses)Param1, (Pipes.PipeSubclasses)Param2, (Pipes.PipeOutpointsRequest*)Param3);
                        if (obtained)
                        {
                            result = SystemCallResults.OK;
                        }
                        else
                        {
                            result = SystemCallResults.Fail;
                        }
                        
                        BasicConsole.WriteLine("DSC: Get Pipe Outpoints - done");
                    }
                    break;
                case SystemCallNumbers.CreatePipe:
                    {
                        BasicConsole.WriteLine("DSC: Create Pipe");

                        bool created = Pipes.PipeManager.CreatePipe(CallerProcess.Id, Param1, (Pipes.CreatePipeRequest*)Param2);
                        if (created)
                        {
                            result = SystemCallResults.OK;
                        }
                        else
                        {
                            result = SystemCallResults.Fail;
                        }

                        BasicConsole.WriteLine("DSC: Create Pipe - done");
                    }
                    break;
                case SystemCallNumbers.WaitOnPipeCreate:
                    {
                        BasicConsole.WriteLine("DSC: Wait On Pipe Create");

                        bool waiting = Pipes.PipeManager.WaitOnPipeCreate(CallerProcess.Id, CallerThread.Id, (Pipes.PipeClasses)Param1, (Pipes.PipeSubclasses)Param2);
                        if (waiting)
                        {
                            result = SystemCallResults.Deferred;
                        }
                        else
                        {
                            result = SystemCallResults.Fail;
                        }

                        BasicConsole.WriteLine("DSC: Wait On Pipe Create - done");
                    }
                    break;
                case SystemCallNumbers.ReadPipe:
                    {
                        BasicConsole.WriteLine("DSC: Read Pipe");

                        // Need access to calling process' memory to be able to set values in request structure(s)
                        MemoryLayout OriginalMemoryLayout = SystemCallsHelpers.EnableAccessToMemoryOfProcess(CallerProcess);

                        Pipes.ReadPipeRequest* RequestPtr = (Pipes.ReadPipeRequest*)Param1;
                        Pipes.PipeManager.RWResults RWResult = Pipes.PipeManager.ReadPipe(RequestPtr->PipeId, RequestPtr->blocking, CallerProcess, CallerThread);

                        // If the request was blocking:
                        if (RequestPtr->blocking)
                        {
                            // Then returning Deferred state from here will leave the caller thread
                            //  in whatever state ReadPipe decided it should be in.
                            result = SystemCallResults.Deferred;
                        }
                        else
                        {
                            if (RWResult == Pipes.PipeManager.RWResults.Complete)
                            {
                                result = SystemCallResults.OK;
                            }
                            else
                            {
                                result = SystemCallResults.Fail;
                            }
                        }

                        SystemCallsHelpers.DisableAccessToMemoryOfProcess(OriginalMemoryLayout);

                        BasicConsole.WriteLine("DSC: Read Pipe - done");
                    }
                    break;
                case SystemCallNumbers.WritePipe:
                    {
                        BasicConsole.WriteLine("DSC: Write Pipe");

                        // Need access to calling process' memory to be able to set values in request structure(s)
                        MemoryLayout OriginalMemoryLayout = SystemCallsHelpers.EnableAccessToMemoryOfProcess(CallerProcess);

                        Pipes.WritePipeRequest* RequestPtr = (Pipes.WritePipeRequest*)Param1;
                        Pipes.PipeManager.RWResults RWResult = Pipes.PipeManager.WritePipe(RequestPtr->PipeId, RequestPtr->blocking, CallerProcess, CallerThread);
                        // If the request was blocking:
                        if (RequestPtr->blocking)
                        {
                            // Then returning Deferred state from here will leave the caller thread
                            //  in whatever state WritePipe decided it should be in.
                            result = SystemCallResults.Deferred;
                        }
                        else
                        {
                            if (RWResult == Pipes.PipeManager.RWResults.Complete)
                            {
                                result = SystemCallResults.OK;
                            }
                            else
                            {
                                result = SystemCallResults.Fail;
                            }
                        }

                        SystemCallsHelpers.DisableAccessToMemoryOfProcess(OriginalMemoryLayout);

                        BasicConsole.WriteLine("DSC: Write Pipe - done");
                    }
                    break;
                default:
                    BasicConsole.WriteLine("DSC: Unrecognised call number.");
                    BasicConsole.WriteLine((uint)syscallNumber);
                    break;
            }

            return result;
        }
        public static void EndDeferredSystemCall(Thread CallerThread, SystemCallResults result, uint Return2, uint Return3, uint Return4)
        {
            CallerThread.Return1 = (uint)result;
            CallerThread.Return2 = Return2;
            CallerThread.Return3 = Return3;
            CallerThread.Return4 = Return4;

            CallerThread._Wake();
        }

        public static void IRQ1(FOS_System.Object state)
        {
            BasicConsole.WriteLine(" ---- IRQ 1 ---- ");
            HandleIRQ(1);
            BasicConsole.WriteLine(" --------------- ");
        }
        private static void HandleIRQ(uint num)
        {
            Process currProcess = ProcessManager.CurrentProcess;
            Thread currThread = ProcessManager.CurrentThread;
            bool switched = false;
            
            Process handlerProcess = null;
            for (int i = 0; i < ProcessManager.Processes.Count; i++)
            {
                handlerProcess = (Process)ProcessManager.Processes[i];
                if (handlerProcess.IRQsToHandle.IsSet((int)num))
                {
                    ProcessManager.SwitchProcess(handlerProcess.Id, ProcessManager.THREAD_DONT_CARE);
                    switched = true;
                    if (handlerProcess.IRQHandler(num) == 0)
                    {
                        break;
                    }
                }
            }

            if (switched)
            {
                ProcessManager.SwitchProcess(currProcess.Id, (int)currThread.Id);
            }
        }
        
        public static int SyscallHandler(uint syscallNumber, uint param1, uint param2, uint param3, 
            ref uint Return2, ref uint Return3, ref uint Return4,
            uint callerProcessId, uint callerThreadId)
        {
            SystemCallResults result = SystemCalls.HandleSystemCallForKernel(syscallNumber, 
                param1, param2, param3, 
                ref Return2, ref Return3, ref Return4,
                callerProcessId, callerThreadId);

            if (result == SystemCallResults.Deferred || result == SystemCallResults.Deferred_PermitActions)
            {
                BasicConsole.WriteLine("Deferring syscall...");
                BasicConsole.WriteLine("Popping unqueued info object...");
                DeferredSyscallInfo info = (DeferredSyscallInfo)DeferredSyscallsInfo_Unqueued.Pop();
                BasicConsole.WriteLine("Setting info...");
                info.ProcessId = callerProcessId;
                info.ThreadId = callerThreadId;

                BasicConsole.WriteLine("Queuing info object...");
                DeferredSyscallsInfo_Queued.Push(info);

                BasicConsole.WriteLine("Waking deferred syscalls thread...");
                DeferredSyscallsThread._Wake();
            }

            return (int)result;
        }
    }
}
