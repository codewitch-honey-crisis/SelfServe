using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace SelfServeDemo
{
	static class Program
	{
		static readonly string _FilePath = Assembly.GetExecutingAssembly().GetModules()[0].FullyQualifiedName;
		static readonly string _File = Assembly.GetExecutingAssembly().GetModules()[0].Name;
		static int Main(string[] args)
		{
			var svctmp = new Service(); // temp instance

			try
			{
				// running as a service
				if (!Environment.UserInteractive)
				{
					bool createdNew = true;
					using (var mutex = new Mutex(true, svctmp.ServiceName, out createdNew))
					{
						if (createdNew)
						{
							mutex.WaitOne();
							ServiceBase[] ServicesToRun;
							ServicesToRun = new ServiceBase[]
							{
								svctmp
							};
							ServiceBase.Run(ServicesToRun);
						}
						else
							throw new ApplicationException("The service " + svctmp.ServiceName + " is already running.");
					}
				}
				else // running from the command line
				{
					if (0 == args.Length)
					{
						_PrintUsage();
						return 0;
					}
					if (1 != args.Length)
					{
						throw new ApplicationException("Too many arguments");
					}
					switch (args[0])
					{
						case "/status":
							_PrintStatus(svctmp.ServiceName);
							break;
						case "/stop":
							_StopService(svctmp.ServiceName, ServiceInstaller.IsInstalled(svctmp.ServiceName));
							break;
						case "/start":
							_StartService(svctmp, ServiceInstaller.IsInstalled(svctmp.ServiceName));
							break;
						case "/install":
							_InstallService(svctmp.ServiceName);
							break;
						case "/uninstall":
							_UninstallService(svctmp.ServiceName);
							break;
					}
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("Error: " + ex.Message);
				return -1;
			}
			return 0;
		}
		static void _InstallService(string name)
		{
			var createdNew = true;
			using (var mutex = new Mutex(true, name, out createdNew))
			{
				if (createdNew)
				{
					mutex.WaitOne();
					ServiceInstaller.Install(name, name, _FilePath);
					Console.Error.WriteLine("Service " + name+ " installed");
				}
				else
				{
					throw new ApplicationException("Service " + name+ " is currently running.");
				}
			}
		}
		static void _UninstallService(string name)
		{
			var createdNew = true;
			using (var mutex = new Mutex(true, name, out createdNew))
			{
				if (createdNew)
				{
					mutex.WaitOne();
					ServiceInstaller.Uninstall(name);
					Console.Error.WriteLine("Service " + name+ " uninstalled");
				}
				else
				{
					throw new ApplicationException("Service " + name+ " is currently running.");
				}
			}
		}
		static void _RunService(Service svc)
		{
			var createdNew = true;
			using (var mutex = new Mutex(true, svc.ServiceName, out createdNew))
			{
				if (createdNew)
				{
					mutex.WaitOne();
					var type = svc.GetType();
					var thread = new Thread(() =>
					{
						// HACK: In order to run this service outside of a service context 
						// we must call the OnStart() protected method directly
						// so we reflect
						var args = new string[0];
						type.InvokeMember("OnStart", BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Instance, null, svc, new object[] { args });
						while (true)
						{
							Thread.Sleep(0);
						}
					});
					thread.Start();
					thread.Join();
					// probably never run, but let's be sure to call it if it does
					type.InvokeMember("OnStop", BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Instance, null, svc, new object[0]);
				}

			}



		}

		static void _StopService(string name, bool isInstalled)
		{
			if (isInstalled)
			{
				ServiceInstaller.StopService(name);
			}
			else
			{
				var id = Process.GetCurrentProcess().Id;
				var procs = Process.GetProcesses();
				for (var i = 0; i < procs.Length; ++i)
				{
					var proc = procs[i];
					var f = proc.ProcessName;
					if (id != proc.Id && 0 == string.Compare(Path.GetFileNameWithoutExtension(_File), f))
					{
						try
						{

							proc.Kill();
							if (!proc.HasExited)
								proc.WaitForExit();
						}
						catch { }
					}
				}
			}
			_PrintStatus(name);
		}
		static void _StartService(Service svc, bool isInstalled)
		{
			if (isInstalled)
			{
				ServiceInstaller.StartService(svc.ServiceName);
				_PrintStatus(svc.ServiceName);
			}
			else
			{
				_PrintStatus(svc.ServiceName,isInstalled,true);
				_RunService(svc);
			}

		}
		static void _PrintStatus(string name)
		{
			var isInstalled = ServiceInstaller.IsInstalled(name);
			var isRunning = true;
			bool createdNew;
			using(var mutex=new Mutex(true,name,out createdNew))
			{
				if(createdNew)
				{
					isRunning = false;
				}
			}
			_PrintStatus(name, isInstalled, isRunning);
		}
		static void _PrintStatus(string name, bool isInstalled, bool isRunning)
		{
			Console.Write(name);
			if (isInstalled)
				Console.Write(" is installed and ");
			else
				Console.Write(" is ");
			if (isRunning)
				Console.WriteLine("running.");
			else
				Console.WriteLine("not running.");
		}
		static void _PrintUsage()
		{
			var t = Console.Error;
			t.Write("Usage: " + _File);
			t.WriteLine(" /start | /stop | /install | /uninstall | /status");
			t.WriteLine();
			t.WriteLine("   /start      Starts the service, if it's not already running. When not installed, this runs in console mode.");
			t.WriteLine("   /stop       Stops the service, if it's running. This will stop the installed service, or kill the console mode service process.");
			t.WriteLine("   /install    Installs the service, if not installed so that it may run in Windows service mode.");
			t.WriteLine("   /uninstall  Uninstalls the service, if installed, so that it will not run in Windows service mode.");
			t.WriteLine("   /status     Reports if the service is installed and/or running.");
			t.WriteLine();

		}
		#region ServiceInstaller and support (adapted from https://stackoverflow.com/questions/358700/how-to-install-a-windows-service-programmatically-in-c)
		public static class ServiceInstaller
		{
			private const int STANDARD_RIGHTS_REQUIRED = 0xF0000;
			private const int SERVICE_WIN32_OWN_PROCESS = 0x00000010;

			[StructLayout(LayoutKind.Sequential)]
			private class SERVICE_STATUS
			{
				public int dwServiceType = 0;
				public _ServiceState dwCurrentState = 0;
				public int dwControlsAccepted = 0;
				public int dwWin32ExitCode = 0;
				public int dwServiceSpecificExitCode = 0;
				public int dwCheckPoint = 0;
				public int dwWaitHint = 0;
			}

			#region OpenSCManager
			[DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
			static extern IntPtr OpenSCManager(string machineName, string databaseName, _ScmAccessRights dwDesiredAccess);
			#endregion

			#region OpenService
			[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
			static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, _ServiceAccessRights dwDesiredAccess);
			#endregion

			#region CreateService
			[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
			private static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName, _ServiceAccessRights dwDesiredAccess, int dwServiceType, _ServiceBootFlag dwStartType, _ServiceError dwErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lp, string lpPassword);
			#endregion

			#region CloseServiceHandle
			[DllImport("advapi32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			static extern bool CloseServiceHandle(IntPtr hSCObject);
			#endregion

			#region QueryServiceStatus
			[DllImport("advapi32.dll")]
			private static extern int QueryServiceStatus(IntPtr hService, SERVICE_STATUS lpServiceStatus);
			#endregion

			#region DeleteService
			[DllImport("advapi32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			private static extern bool DeleteService(IntPtr hService);
			#endregion

			#region ControlService
			[DllImport("advapi32.dll")]
			private static extern int ControlService(IntPtr hService, _ServiceControl dwControl, SERVICE_STATUS lpServiceStatus);
			#endregion

			#region StartService
			[DllImport("advapi32.dll", SetLastError = true)]
			private static extern int StartService(IntPtr hService, int dwNumServiceArgs, int lpServiceArgVectors);
			#endregion

			public static void Uninstall(string serviceName)
			{
				IntPtr scm = _OpenSCManager(_ScmAccessRights.AllAccess);

				try
				{
					IntPtr service = OpenService(scm, serviceName, _ServiceAccessRights.AllAccess);
					if (service == IntPtr.Zero)
						throw new ApplicationException("Service not installed.");

					try
					{
						_StopService(service);
						if (!DeleteService(service))
							throw new ApplicationException("Could not delete service " + Marshal.GetLastWin32Error());
					}
					finally
					{
						CloseServiceHandle(service);
					}
				}
				finally
				{
					CloseServiceHandle(scm);
				}
			}

			public static bool IsInstalled(string serviceName)
			{
				IntPtr scm = _OpenSCManager(_ScmAccessRights.Connect);

				try
				{
					IntPtr service = OpenService(scm, serviceName, _ServiceAccessRights.QueryStatus);

					if (service == IntPtr.Zero)
						return false;

					CloseServiceHandle(service);
					return true;
				}
				finally
				{
					CloseServiceHandle(scm);
				}
			}

			public static void Install(string serviceName, string displayName, string fileName)
			{
				IntPtr scm = _OpenSCManager(_ScmAccessRights.AllAccess);

				try
				{
					IntPtr service = OpenService(scm, serviceName, _ServiceAccessRights.AllAccess);

					if (service == IntPtr.Zero)
						service = CreateService(scm, serviceName, displayName, _ServiceAccessRights.AllAccess, SERVICE_WIN32_OWN_PROCESS, _ServiceBootFlag.AutoStart, _ServiceError.Normal, fileName, null, IntPtr.Zero, null, null, null);

					if (service == IntPtr.Zero)
						throw new ApplicationException("Failed to install service.");

					/*try
                    {
                        StartService(service);
                    }
                    finally
                    {
                        CloseServiceHandle(service);
                    }*/
				}
				finally
				{
					CloseServiceHandle(scm);
				}
			}

			public static void StartService(string serviceName)
			{
				IntPtr scm = _OpenSCManager(_ScmAccessRights.Connect);

				try
				{
					IntPtr service = OpenService(scm, serviceName, _ServiceAccessRights.QueryStatus | _ServiceAccessRights.Start);
					if (service == IntPtr.Zero)
						throw new ApplicationException("Could not open service.");

					try
					{
						_StartService(service);
					}
					finally
					{
						CloseServiceHandle(service);
					}
				}
				finally
				{
					CloseServiceHandle(scm);
				}
			}

			public static void StopService(string serviceName)
			{
				IntPtr scm = _OpenSCManager(_ScmAccessRights.Connect);

				try
				{
					IntPtr service = OpenService(scm, serviceName, _ServiceAccessRights.QueryStatus | _ServiceAccessRights.Stop);
					if (service == IntPtr.Zero)
						throw new ApplicationException("Could not open service.");

					try
					{
						_StopService(service);
					}
					finally
					{
						CloseServiceHandle(service);
					}
				}
				finally
				{
					CloseServiceHandle(scm);
				}
			}

			static void _StartService(IntPtr service)
			{
				SERVICE_STATUS status = new SERVICE_STATUS();
				StartService(service, 0, 0);
				var changedStatus = _WaitForServiceStatus(service, _ServiceState.StartPending, _ServiceState.Running);
				if (!changedStatus)
					throw new ApplicationException("Unable to start service");
			}

			static void _StopService(IntPtr service)
			{
				SERVICE_STATUS status = new SERVICE_STATUS();
				ControlService(service, _ServiceControl.Stop, status);
				var changedStatus = _WaitForServiceStatus(service, _ServiceState.StopPending, _ServiceState.Stopped);
				if (!changedStatus)
					throw new ApplicationException("Unable to stop service");
			}

			static _ServiceState _GetServiceStatus(IntPtr service)
			{
				SERVICE_STATUS status = new SERVICE_STATUS();

				if (QueryServiceStatus(service, status) == 0)
					throw new ApplicationException("Failed to query service status.");

				return status.dwCurrentState;
			}

			static bool _WaitForServiceStatus(IntPtr service, _ServiceState waitStatus, _ServiceState desiredStatus)
			{
				SERVICE_STATUS status = new SERVICE_STATUS();

				QueryServiceStatus(service, status);
				if (status.dwCurrentState == desiredStatus) return true;

				int dwStartTickCount = Environment.TickCount;
				int dwOldCheckPoint = status.dwCheckPoint;

				while (status.dwCurrentState == waitStatus)
				{
					// Do not wait longer than the wait hint. A good interval is
					// one tenth the wait hint, but no less than 1 second and no
					// more than 10 seconds.

					int dwWaitTime = status.dwWaitHint / 10;

					if (dwWaitTime < 1000) dwWaitTime = 1000;
					else if (dwWaitTime > 10000) dwWaitTime = 10000;

					Thread.Sleep(dwWaitTime);

					// Check the status again.

					if (QueryServiceStatus(service, status) == 0) break;

					if (status.dwCheckPoint > dwOldCheckPoint)
					{
						// The service is making progress.
						dwStartTickCount = Environment.TickCount;
						dwOldCheckPoint = status.dwCheckPoint;
					}
					else
					{
						if (Environment.TickCount - dwStartTickCount > status.dwWaitHint)
						{
							// No progress made within the wait hint
							break;
						}
					}
				}
				return (status.dwCurrentState == desiredStatus);
			}

			static IntPtr _OpenSCManager(_ScmAccessRights rights)
			{
				IntPtr scm = OpenSCManager(null, null, rights);
				if (scm == IntPtr.Zero)
					throw new ApplicationException("Could not connect to service control manager.");

				return scm;
			}
		}


		private enum _ServiceState
		{
			Unknown = -1, // The state cannot be (has not been) retrieved.
			NotFound = 0, // The service is not known on the host server.
			Stopped = 1,
			StartPending = 2,
			StopPending = 3,
			Running = 4,
			ContinuePending = 5,
			PausePending = 6,
			Paused = 7
		}

		[Flags]
		private enum _ScmAccessRights
		{
			Connect = 0x0001,
			CreateService = 0x0002,
			EnumerateService = 0x0004,
			Lock = 0x0008,
			QueryLockStatus = 0x0010,
			ModifyBootConfig = 0x0020,
			StandardRightsRequired = 0xF0000,
			AllAccess = (StandardRightsRequired | Connect | CreateService |
						 EnumerateService | Lock | QueryLockStatus | ModifyBootConfig)
		}

		[Flags]
		private enum _ServiceAccessRights
		{
			QueryConfig = 0x1,
			ChangeConfig = 0x2,
			QueryStatus = 0x4,
			EnumerateDependants = 0x8,
			Start = 0x10,
			Stop = 0x20,
			PauseContinue = 0x40,
			Interrogate = 0x80,
			UserDefinedControl = 0x100,
			Delete = 0x00010000,
			StandardRightsRequired = 0xF0000,
			AllAccess = (StandardRightsRequired | QueryConfig | ChangeConfig |
						 QueryStatus | EnumerateDependants | Start | Stop | PauseContinue |
						 Interrogate | UserDefinedControl)
		}

		private enum _ServiceBootFlag
		{
			Start = 0x00000000,
			SystemStart = 0x00000001,
			AutoStart = 0x00000002,
			DemandStart = 0x00000003,
			Disabled = 0x00000004
		}

		private enum _ServiceControl
		{
			Stop = 0x00000001,
			Pause = 0x00000002,
			Continue = 0x00000003,
			Interrogate = 0x00000004,
			Shutdown = 0x00000005,
			ParamChange = 0x00000006,
			NetBindAdd = 0x00000007,
			NetBindRemove = 0x00000008,
			NetBindEnable = 0x00000009,
			NetBindDisable = 0x0000000A
		}

		private enum _ServiceError
		{
			Ignore = 0x00000000,
			Normal = 0x00000001,
			Severe = 0x00000002,
			Critical = 0x00000003
		}
		#endregion
	}
}
