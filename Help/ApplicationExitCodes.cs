namespace Loki.Game
{
	/// <summary>
	/// Return values for ApplicationExitCode.
	/// </summary>
	public enum ApplicationExitCodes
	{
		/// <summary>No error.</summary>
		None = 0,

		/// <summary>There was a patch and EB is not usable.</summary>
		UnsupportedClientVersion = 1,

		/// <summary>The offsets required to run the bot are not uploaded yet.</summary>
		OffsetsMissing = 2,

		/// <summary>The Buddy updater was attempted to be downloaded, but the file was not found.</summary>
		UpdaterNotFound = 3,

		/// <summary>An exception was thrown during the update process.</summary>
		UpdateException = 4,

		/// <summary>Exilebuddy is exiting to perform an update.</summary>
		Updating = 5,

		/// <summary>Exilebuddy is restarting.</summary>
		Restarting = 6,

		/// <summary>Compile errors were encountered.</summary>
		CompileErrors = 7,

		/// <summary>The offsets required to run were not obtained due to an auth error.</summary>
		AuthError = 8,

		/// <summary>Load errors were encountered.</summary>
		LoadErrors = 9,

		/// <summary>An unknown error occurred, please check the logs.</summary>
		Unknown = 998,

		/// <summary>The program cannot run because prerequisites are missing.</summary>
		MissingPrerequisites = 999,
	}
}