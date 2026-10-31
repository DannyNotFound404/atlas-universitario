using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BackupNAS;

internal static class WNetHelper
{
	private const int MAX_PREFERRED_LENGTH = -1;

	private const int ERROR_MORE_DATA = 234;

	private const int NERR_Success = 0;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct NETRESOURCE
	{
		public int dwScope;

		public int dwType;

		public int dwDisplayType;

		public int dwUsage;

		public string? lpLocalName;

		public string? lpRemoteName;

		public string? lpComment;

		public string? lpProvider;
	}

	public const int RESOURCETYPE_DISK = 1;

	public const int CONNECT_TEMPORARY = 4;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct USE_INFO_0
	{
		public string? ui0_local;

		public string? ui0_remote;
	}

	[DllImport("mpr.dll", CharSet = CharSet.Unicode)]
	public static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource, string lpPassword, string lpUserName, int dwFlags);

	[DllImport("mpr.dll", CharSet = CharSet.Unicode)]
	public static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);

	[DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int NetUseEnum(string? UncServerName, int Level, out IntPtr BufPtr, int PreferedMaximumSize, out int EntriesRead, out int TotalEntries, ref int ResumeHandle);

	[DllImport("Netapi32.dll")]
	private static extern int NetApiBufferFree(IntPtr Buffer);

	public static IReadOnlyList<string> GetConnectedRemotePaths()
	{
		List<string> paths = new List<string>();
		int resumeHandle = 0;
		int result;
		do
		{
			IntPtr buffer = IntPtr.Zero;
			result = NetUseEnum(null, 0, out buffer, MAX_PREFERRED_LENGTH, out int entriesRead, out _, ref resumeHandle);
			try
			{
				if (result != NERR_Success && result != ERROR_MORE_DATA)
				{
					throw new System.ComponentModel.Win32Exception(result, "No se pudo verificar las conexiones abiertas. No se modificó ninguna conexión.");
				}
				int structureSize = Marshal.SizeOf<USE_INFO_0>();
				IntPtr current = buffer;
				for (int i = 0; i < entriesRead; i++)
				{
					USE_INFO_0 entry = Marshal.PtrToStructure<USE_INFO_0>(current);
					if (!string.IsNullOrWhiteSpace(entry.ui0_remote))
					{
						paths.Add(entry.ui0_remote);
					}
					current = IntPtr.Add(current, structureSize);
				}
			}
			finally
			{
				if (buffer != IntPtr.Zero)
				{
					NetApiBufferFree(buffer);
				}
			}
		}
		while (result == ERROR_MORE_DATA);
		return paths;
	}
}
