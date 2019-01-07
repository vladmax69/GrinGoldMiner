﻿using SharedSerialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mozkomor.GrinGoldMiner
{
    public class Worker
    {
        private const bool DEBUG = false;

        private Process worker;
        private TcpClient client;
        private NetworkStream stream;
        private Task listener;
        private AutoResetEvent flushEvent;

        private WorkerType type;
        private int workerDeviceID;
        private int workerPlatformID;
        private int workerCommPort;

        private DateTime workerStartTime = DateTime.Now;
        private DateTime workerLastMessage = DateTime.Now;
        private long workerTotalSolutions = 0;
        private long workerTotalLogs = 0;
        private volatile bool IsTerminated;

        private SharedSerialization.LogMessage lastLog = new LogMessage() { message = "-", time = DateTime.MinValue };
        private SharedSerialization.LogMessage lastDebugLog;
        private SharedSerialization.LogMessage lastErrLog = null;
        private Solution lastSolution = null;
        private volatile uint totalSols = 0;

        private int errors = 0;

        public int ID { get; }

        private GPUOption gpu;

        public Worker(WorkerType gpuType, int gpuID, int platformID)
        {
            type = gpuType;
            workerDeviceID = gpuID;
            workerPlatformID = platformID;
            workerCommPort = 13500 + (int)gpuType + gpuID * platformID;
        }

        public Worker(GPUOption gpu, int id)
        {
            this.ID = id;
            this.gpu = gpu;
            type = gpu.GPUType;
            workerDeviceID = gpu.DeviceID;
            workerPlatformID = gpu.PlatformID;
            workerCommPort = 13500 + (int)gpu.GPUType + id;
        }

        private float GetGPS()
        {
            if (lastSolution != null && lastSolution.job != null)
            {
                var interval = lastSolution.job.solvedAt - lastSolution.job.timestamp;
                var attempts = lastSolution.job.graphAttempts;

                if (interval.TotalSeconds > 0)
                    return (float)attempts / (float)interval.TotalSeconds;
                return 0;
            }
            else
                return 0;
        }

        public void PrintStatusLinesToConsole()
        {
            try
            {
                Console.Write($"GPU {ID}: {gpu.GPUName}");
                Console.CursorLeft = 30;
                switch (GetStatus())
                {
                    case GPUStatus.STARTING:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"STARTING");
                        break;
                    case GPUStatus.ONLINE:
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write($"ONLINE");
                        break;
                    case GPUStatus.OFFLINE:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"OFFLINE");
                        break;
                    case GPUStatus.DISABLED:
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"DISABLED");
                        break;
                    case GPUStatus.ERROR:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"ERROR");
                        break;
                }
                Console.ResetColor();
                Console.CursorLeft = 45;
                Console.Write($"Mining at {GetGPS():F2} gps");
                Console.CursorLeft = 75;
                Console.WriteLine($"Solutions: {totalSols}");
                WipeLine();
                Console.CursorLeft = 7; Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"Last Message: {lastLog.ToShortString()}"); Console.ResetColor();
                WipeLine();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR GPU {ID}, MSG: {ex.Message}");
                Console.ResetColor();
                WipeLine();
            }
        }

        private static void WipeLine()
        {
            Console.Write("                                                                                                              ");
            Console.CursorLeft = 0;
        }

        private GPUStatus GetStatus()
        {
            try
            {
                if (!gpu.Enabled)
                    return GPUStatus.DISABLED;
                else if (lastErrLog != null)
                    return GPUStatus.ERROR;
                else if (lastLog.time == DateTime.MinValue || lastSolution == null)
                    return GPUStatus.STARTING;
                else if (lastSolution.job.timestamp.AddMinutes(15) < DateTime.Now)
                    return GPUStatus.OFFLINE;
                else
                    return GPUStatus.ONLINE;
            }
            catch
            {
                return GPUStatus.ERROR;
            }
        }

        public List<GpuDevice> GetDevices()
        {
            try
            {
                TcpListener l = new TcpListener(IPAddress.Parse("127.0.0.1"), 13500);
                l.Start();
                var process = Process.Start(new ProcessStartInfo()
                {
                    FileName = (type == WorkerType.NVIDIA) ? Path.Combine("solvers", "CudaSolver.exe") : Path.Combine("solvers", "OclSolver.exe"),
                    Arguments = string.Format("-1 13500"),
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                var client = l.AcceptTcpClient();
                NetworkStream stream = client.GetStream();
                l.Stop();
                object devices = (new BinaryFormatter()).Deserialize(stream);
                try
                {
                    stream.Close();
                    client.Close();
                    process.Kill();
                }
                catch { }
                if (devices is GpuDevicesMessage)
                    return (devices as GpuDevicesMessage).devices;
                else
                    return new List<GpuDevice>(); // and log ?!
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.ERROR, "Failed to enumerate devices: " + ex.Message);
                return new List<GpuDevice>();
            }
        }

        //send job to worker
        public bool SendJob(SharedSerialization.Job job)
        {
            try
            {
                (new BinaryFormatter() { AssemblyFormat = System.Runtime.Serialization.Formatters.FormatterAssemblyStyle.Simple }).Serialize(stream, job);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(ex);
                return false;
            }
        }

        public bool Start()
        {
            try
            {
                if (!gpu.Enabled)
                    return true;

#if DEBUG
                TcpListener l = new TcpListener(IPAddress.Parse("0.0.0.0"), workerCommPort);
#else
                TcpListener l = new TcpListener(IPAddress.Parse("127.0.0.1"), workerCommPort);
#endif
                l.Start();
                worker = Process.Start(new ProcessStartInfo()
                {
                    FileName = (type == WorkerType.NVIDIA) ? Path.Combine("solvers", "CudaSolver.exe") : Path.Combine("solvers", "OclSolver.exe"),
                    Arguments = string.Format("{0} {1} {2}", workerDeviceID, workerCommPort, workerPlatformID),
                    CreateNoWindow = true,
                    UseShellExecute = false
                    //, RedirectStandardOutput =true
                    //WindowStyle = ProcessWindowStyle.Hidden
                    
                });
                client = l.AcceptTcpClient();
                l.Stop();
                stream = client.GetStream();
                listener = Task.Factory.StartNew(() => { Listen(); }, TaskCreationOptions.LongRunning);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.ERROR, "Failed to start worker process: " + ex.Message);
                return false;
            }
        }

        //receive solution from worker
        private void Listen()
        {
            while (!IsTerminated)
            {
                try
                {
                    object payload = (new BinaryFormatter()).Deserialize(stream);

                    switch (payload)
                    {
                        case SharedSerialization.Solution sol:
                            totalSols++;
                            lastSolution = sol;
                            WorkerManager.SubmitSolution(sol);
                            break;
                        case SharedSerialization.LogMessage log:
                            if (log.level == SharedSerialization.LogLevel.Debug)
                                lastDebugLog = log;
                            else if (log.level == SharedSerialization.LogLevel.Error)
                                lastErrLog = lastLog = log;
                            else
                                lastLog = log;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.ERROR, "Listen error" + ex.Message);

                    if (errors++ > 6)
                        IsTerminated = true;
                }
            }
        }

        public void Close()
        {
            try
            {
                IsTerminated = true;
                if (stream != null)
                    stream.Close();
                if (client != null)
                    client.Close();
            }
            catch { }
        }
    }

    public enum GPUStatus
    {
        STARTING,
        DISABLED,
        ONLINE,
        OFFLINE,
        ERROR
    }
}
