using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading;

/*
 * This class has a lot of async stuff going on. It handles updating the launcher
 * and loading the changelog from the server.
 * Since this class starts new threads in which it does the larger computations,
 * there must be no useage of UI code in this class. Keep it clean!
 * 
 */

namespace Launchpad
{
	/// <summary>
	/// This class has a lot of async stuff going on. It handles updating the launcher
	/// and loading the changelog from the server.
	/// Since this class starts new threads in which it does the larger computations,
	/// there must be no useage of UI code in this class. Keep it clean!
	/// </summary>
	public sealed class LauncherHandler
	{


		/// <summary>
		/// Occurs when changelog download progress changes.
		/// </summary>
		public event LaunchpadEventDelegates.ChangelogProgressChangedEventHandler ChangelogProgressChanged;
		/// <summary>
		/// Occurs when changelog download finishes.
		/// </summary>
		public event LaunchpadEventDelegates.ChangelogDownloadFinishedEventHandler ChangelogDownloadFinished;

		/// <summary>
		/// The progress arguments object. Is updated during file download operations.
		/// </summary>
		private FileDownloadProgressChangedEventArgs ProgressArgs;
		/// <summary>
		/// The download finished arguments object. Is updated once a file download finishes.
		/// </summary>
		private GameDownloadFinishedEventArgs DownloadFinishedArgs;

		/// <summary>
		/// The config handler reference.
		/// </summary>
		ConfigHandler Config = ConfigHandler._instance;

		/// <summary>
		/// Initializes a new instance of the <see cref="Launchpad_Launcher.LauncherHandler"/> class.
		/// </summary>
		public LauncherHandler ()
		{
			ProgressArgs = new FileDownloadProgressChangedEventArgs ();
			DownloadFinishedArgs = new GameDownloadFinishedEventArgs ();
		}

		/// <summary>
		/// Updates the launcher synchronously.
		/// </summary>
		public void UpdateLauncher()
		{
			try
			{
				FTPHandler FTP = new FTPHandler ();
				string fullName = Assembly.GetEntryAssembly().Location;
				string executableName = Path.GetFileName(fullName); // should be "Launchpad", unless the user has renamed the file

				string local = String.Format("{0}{1}", 
				                             Config.GetTempDir(), 
				                             executableName);

				//download the new launcher binary to the system's temp dir
				FTP.DownloadFTPFile(Config.GetLauncherBinaryURL(), local, false);
				//first, create a script that will update our launcher
				ProcessStartInfo script = CreateUpdateScript ();

				Process.Start(script);
				Environment.Exit(0);
			}
			catch (Exception ex)
			{
				Console.WriteLine ("UpdateLauncher(): " + ex.Message);
			}
		}

		/// <summary>
		/// Downloads the manifest.
		/// </summary>
		public void DownloadManifest()
		{
			Stream manifestStream = null;														
			try
			{
				FTPHandler FTP = new FTPHandler ();
				MD5Handler MD5 = new MD5Handler ();

				string remoteChecksum = FTP.GetRemoteManifestChecksum ();
				string localChecksum = "";

				string remote = Config.GetManifestURL ();
				string local = Config.GetManifestPath ();

				if (File.Exists(Config.GetManifestPath()))
				{
					manifestStream = File.OpenRead (Config.GetManifestPath ());
					localChecksum = MD5.GetFileHash (manifestStream);

					if (!(remoteChecksum == localChecksum))
					{
						//Copy the old manifest so that we can compare them when updating the game
						File.Copy(local, local + ".old");

						FTP.DownloadFTPFile (remote, local, false);
					}
				}
				else
				{
					FTP.DownloadFTPFile (remote, local, false);
				}						
			}
			catch (Exception ex)
			{
				Console.WriteLine ("DownloadManifest(): " + ex.Message);
			}
			finally
			{
				if (manifestStream != null)
				{
					manifestStream.Close ();
				}
			}
		}

		/// <summary>
		/// Gets the changelog from the server asynchronously.
		/// </summary>
		public void LoadChangelog()
		{
			Thread t = new Thread (LoadChangelogAsync);
			t.Start ();

		}
		private void LoadChangelogAsync()
		{
			FTPHandler FTP = new FTPHandler ();

			//load the HTML from the server as a string
			string content = FTP.ReadFTPFile (Config.GetChangelogURL ());
					
			DownloadFinishedArgs.Result = content;
			DownloadFinishedArgs.Metadata = Config.GetChangelogURL ();

			OnChangelogDownloadFinished ();
		}

		/// <summary>
		/// Creates the update script on disk.
		/// </summary>
		/// <returns>ProcessStartInfo for the update script.</returns>
		private ProcessStartInfo CreateUpdateScript()
		{
			try
			{
				ChecksHandler Checks = new ChecksHandler ();

				//maintain the executable name if it was renamed to something other than 'Launchpad' 
				string fullName = Assembly.GetEntryAssembly().Location;
				string executableName = Path.GetFileName(fullName); // should be "Launchpad", unless the user has renamed it

				if (Checks.IsRunningOnUnix())
				{
					//creating a .sh script
					string scriptPath = String.Format (@"{0}launchpadupdate.sh", 
					                                   Config.GetTempDir ()) ;


					FileStream updateScript = File.Create (scriptPath);
					TextWriter tw = new StreamWriter (updateScript);

					//write commands to the script
					//wait five seconds, then copy the new executable
					string copyCom = String.Format ("mv -f {0} {1}", 
					                                Config.GetTempDir() + executableName,
					                                Config.GetLocalDir() + executableName);

					string dirCom = String.Format ("cd {0}", Config.GetLocalDir ());
					string launchCom = String.Format (@"nohup ./{0} &", executableName);
					tw.WriteLine (@"#!/bin/sh");
					tw.WriteLine ("sleep 5");
					tw.WriteLine (copyCom);
					tw.WriteLine (dirCom);
					tw.WriteLine("chmod +x " + executableName);
					tw.WriteLine (launchCom);
					tw.Close();

					UnixHandler Unix = new UnixHandler();
					Unix.MakeExecutable(scriptPath);


					//Now create some ProcessStartInfo for this script
					ProcessStartInfo updateShellProcess = new ProcessStartInfo ();
									
					updateShellProcess.FileName = scriptPath;
					updateShellProcess.UseShellExecute = false;
					updateShellProcess.RedirectStandardOutput = false;
					updateShellProcess.WindowStyle = ProcessWindowStyle.Hidden;

					return updateShellProcess;
				}
				else
				{
					//creating a .bat script
					string scriptPath = String.Format (@"{0}{1}update.bat", 
					                                   Config.GetLocalDir (), 
					                                   Path.DirectorySeparatorChar);

					FileStream updateScript = File.Create(scriptPath);

					TextWriter tw = new StreamWriter(updateScript);

					//write commands to the script
					//wait three seconds, then copy the new executable
					tw.WriteLine(String.Format(@"timeout 3 & xcopy /s /y ""{0}\{2}"" ""{1}\{2}"" && del ""{0}\{2}""", 
					                           Config.GetTempDir(), 
					                           Config.GetLocalDir(), 
					                           executableName));
					//then start the new executable
					tw.WriteLine(String.Format(@"start {0}", executableName));
					tw.Close();

					ProcessStartInfo updateBatchProcess = new ProcessStartInfo();

					updateBatchProcess.FileName = scriptPath;
					updateBatchProcess.UseShellExecute = true;
					updateBatchProcess.RedirectStandardOutput = false;
					updateBatchProcess.WindowStyle = ProcessWindowStyle.Hidden;

					return updateBatchProcess;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine ("CreateUpdateScript(): " + ex.Message);

				return new ProcessStartInfo ();
			}
		}

		/// <summary>
		/// Raises the changelog progress changed event.
		/// </summary>
		private void OnChangelogProgressChanged()
		{
			if (ChangelogProgressChanged != null)
			{
				//raise the event
				ChangelogProgressChanged (this, ProgressArgs);
			}
		}

		/// <summary>
		/// Raises the changelog download finished event.
		/// </summary>
		private void OnChangelogDownloadFinished()
		{
			if (ChangelogDownloadFinished != null)
			{
				//raise the event
				ChangelogDownloadFinished (this, DownloadFinishedArgs);
			}
		}
	}
}

