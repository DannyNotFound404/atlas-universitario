using Atlas.Desktop;
using Miguel.Contracts;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BackupNAS;

public class MainForm : Form
{
    private readonly bool previewMode;

	private Panel pnlHeader = new Panel();

	private Label lblTitulo = new Label();

	private Label lblSubtit = new Label();

	private Label lblInfo = new Label();

	private Label lblUser = new Label();

	private TextBox txtUser = new TextBox();

	private Label lblPass = new Label();

	private TextBox txtPass = new TextBox();

	private PasswordRevealButton btnRevealPassword = new PasswordRevealButton();

	private Label lblEstado = new Label();

	private Label lblProgresoGeneral = new Label();

	private Label lblProgresoGeneralPercent = new Label();

	private ModernProgressBar progress = new ModernProgressBar();

	private Label lblArchivoActual = new Label();

	private Label lblArchivoPercent = new Label();

	private ModernProgressBar progressArchivo = new ModernProgressBar();

	private Label lblActividad = new Label();

	private Label lblStatusIcon = new Label();

	private Label lblStatusTitle = new Label();

	private Label lblStatusDescription = new Label();

	private Label lblStatusBadge = new Label();

	private Label lblError = new Label();

	private ModernButton btnIniciar = new ModernButton();

	private ModernButton btnCancelar = new ModernButton();

	private Label lblVersion = new Label();

	private string nasIP = "";

	private string nasShare = "";

	private string nasPath = "";

	private List<string> carpetas = new List<string>();

	private string userProfile = "";

	private bool excluirOcultos = true;

	private bool excluirTemporales = true;

	private BackgroundWorker worker = new BackgroundWorker();

	private bool isRunning;

	private bool connectedByThisApp;
    private readonly NasConnectionSession nasConnection = new();

	private readonly object processLock = new object();

	private Process? activeRobocopy;

	private string currentLogPath = "";

	private readonly System.Windows.Forms.Timer activityTimer = new System.Windows.Forms.Timer();

	private DateTime currentFileStartedUtc;

	private DateTime lastRobocopyActivityUtc;

	private string currentRobocopyPhase = "";

	private string currentActivityFile = "";

	private DateTimeOffset currentBackupStartedAtUtc;
	private BackupRunObserver? runObserver;

	private string currentSnapshotMode = "live";

	private string? currentSnapshotWarning;

	private DateTime lastPendingUpdateCheckUtc;

	private bool pendingUpdateNoticeVisible;

	private bool closingForPendingUpdate;

	private static readonly string PendingUpdateMarkerPath = GetPendingUpdateMarkerPath();

	private static readonly Regex RobocopyPercentRegex = new Regex("(?<percent>\\d{1,3}(?:[.,]\\d+)?)%", RegexOptions.Compiled);

	private static readonly Regex RobocopyEtaSuffixRegex = new Regex(
		@"\s+\d{1,2}:\d{2}(?::\d{2})?\s*->\s*\d{1,2}:\d{2}(?::\d{2})?\s*$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly HashSet<string> TemporaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".tmp",
		".temp",
		".partial",
		".crdownload",
		".download"
	};

	private sealed class BackupRequest
	{
		public string User { get; }

		public string Password { get; }

		public BackupRequest(string user, string password)
		{
			User = user;
			Password = password;
		}
	}

	private sealed class BackupProgress
	{
		public string Status { get; }

		public double FileProgress { get; }

		public string CurrentFile { get; }

		public BackupProgress(string status, double fileProgress, string currentFile)
		{
			Status = status;
			FileProgress = fileProgress;
			CurrentFile = currentFile;
		}
	}

	private sealed class BackupResult
	{
		public List<string> FailedFolders { get; } = new List<string>();

		public List<string> MissingFolders { get; } = new List<string>();

		public string LogPath { get; }

		public string SnapshotMode { get; set; } = "live";

		public string? SnapshotWarning { get; set; }

		public BackupResult(string logPath)
		{
			LogPath = logPath;
		}
	}

	public MainForm()
		: this(previewMode: false)
	{
	}

	internal MainForm(bool previewMode)
	{
        this.previewMode = previewMode;
		userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (previewMode)
		{
			BuildUI();
			BuildWorker();
			return;
		}
		try { BackupReportDelivery.Retry(); } catch (Exception ex) { Trace.WriteLine(ex.GetType().Name); }
		if (!LoadConfig())
		{
			runObserver = CreateRunObserver();
			runObserver.Finish(error: new InvalidOperationException("CONFIGURATION_INVALID"));
			base.Load += delegate
			{
				Close();
			};
		}
		else if (!CheckSMBConnection())
		{
			runObserver = CreateRunObserver();
			runObserver.Finish(error: new IOException("NAS_UNREACHABLE_BEFORE_START"));
			base.Load += delegate
			{
				Close();
			};
		}
		else
		{
			BuildUI();
			BuildWorker();
		}
	}

	private static string LongPath(string path)
	{
		if (path.StartsWith("\\\\?\\"))
		{
			return path;
		}
		if (path.StartsWith("\\\\"))
		{
			return "\\\\?\\UNC\\" + path.Substring(2, path.Length - 2);
		}
		return "\\\\?\\" + path;
	}

	private bool LoadConfig()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "config.txt");
		if (!File.Exists(path))
		{
			MessageBox.Show("No se encontro el archivo config.txt.\n\nRepare la instalacion de BackupNAS o contacte a Tecnologia e Informatica.", "Backup NAS - Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		string[] array = File.ReadAllLines(path);
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith('#') || text == "")
			{
				continue;
			}
			if (text.StartsWith("NAS_IP="))
			{
				string text2 = text;
				int length = "NAS_IP=".Length;
				nasIP = text2.Substring(length, text2.Length - length).Trim();
			}
			else if (text.StartsWith("NAS_SHARE="))
			{
				string text2 = text;
				int length = "NAS_SHARE=".Length;
				nasShare = text2.Substring(length, text2.Length - length).Trim();
			}
			else if (text.StartsWith("EXCLUIR_OCULTOS="))
			{
				excluirOcultos = ParseConfigBoolean(text.Substring("EXCLUIR_OCULTOS=".Length), defaultValue: true);
			}
			else if (text.StartsWith("EXCLUIR_TEMPORALES="))
			{
				excluirTemporales = ParseConfigBoolean(text.Substring("EXCLUIR_TEMPORALES=".Length), defaultValue: true);
			}
			else if (Regex.IsMatch(text, "^CARPETA\\d+="))
			{
				int num = text.IndexOf('=');
				string text2 = text;
				int length = num + 1;
				string text3 = text2.Substring(length, text2.Length - length).Trim();
				if (text3 != "")
				{
					carpetas.Add(text3);
				}
			}
		}
		if (string.IsNullOrEmpty(nasIP) || string.IsNullOrEmpty(nasShare))
		{
			MessageBox.Show("El archivo config.txt esta incompleto.\n\nVerifique NAS_IP y NAS_SHARE.", "Backup NAS - Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		if (carpetas.Count == 0)
		{
			MessageBox.Show("No hay carpetas configuradas.\n\nAgregue al menos CARPETA1=...", "Backup NAS - Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		nasPath = "\\\\" + nasIP + "\\" + nasShare;
		return true;
	}

	private static bool ParseConfigBoolean(string value, bool defaultValue)
	{
		string normalized = value.Trim();
		if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase) || normalized.Equals("si", StringComparison.OrdinalIgnoreCase) || normalized == "1")
		{
			return true;
		}
		if (normalized.Equals("false", StringComparison.OrdinalIgnoreCase) || normalized.Equals("no", StringComparison.OrdinalIgnoreCase) || normalized == "0")
		{
			return false;
		}
		return defaultValue;
	}

	private bool CheckSMBConnection()
	{
		try
		{
			using TcpClient tcpClient = new TcpClient();
			IAsyncResult asyncResult = tcpClient.BeginConnect(nasIP, 445, null, null);
			if (!asyncResult.AsyncWaitHandle.WaitOne(3000, exitContext: false))
			{
				MessageBox.Show("No se encontro el servidor NAS en esta red.\n\nIP: " + nasIP + "\n\nLa copia de seguridad se omitira por hoy.", "Backup NAS - Sin conexion", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return false;
			}
			tcpClient.EndConnect(asyncResult);
		}
		catch
		{
			MessageBox.Show("Error al verificar conexion con el NAS.\nIP: " + nasIP, "Backup NAS - Sin conexion", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return false;
		}
		return true;
	}

	private void ConnectNAS(string user, string pass)
	{
		connectedByThisApp = false;
        int result = nasConnection.Connect(nasPath, user, pass);
		if (result != 0)
		{
			throw new Exception(TranslateError(result));
		}
        connectedByThisApp = nasConnection.OwnsConnection;
	}

	private void DisconnectNAS()
	{
		if (!connectedByThisApp)
		{
			return;
		}
		try
		{
            int result = nasConnection.Disconnect();
            if (result != 0) Trace.WriteLine("ATLAS: la sesión NAS permanece abierta porque Windows la mantiene en uso (" + result + ").");
		}
		catch
		{
		}
		finally
		{
			connectedByThisApp = false;
		}
	}

	private string TranslateError(int code)
	{
		return code switch
		{
			1326 => "Credenciales incorrectas.\nVerifique usuario y contrasena.", 
			86 => "Contrasena incorrecta.", 
			5 => "Acceso denegado.\nEl usuario no tiene permisos sobre este recurso.", 
			2202 => "Nombre de usuario invalido.", 
			1219 => "Windows ya está conectado a este NAS con otra cuenta.\nLa copia no se inició y tus conexiones abiertas se conservaron. Tecnología puede revisar la cuenta de acceso al destino.", 
			53 => "No se encontro el servidor NAS.\nVerifique NAS_IP en config.txt.", 
			67 => "Nombre de red incorrecto.\nVerifique NAS_SHARE en config.txt.", 
			1200 => "Ruta no accesible.\nVerifique NAS_IP y NAS_SHARE en config.txt.", 
			_ => $"Error Win32: {code}\nRuta: {nasPath}", 
		};
	}

	private static string CreateLogPath()
	{
		string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupNAS", "Logs");
		Directory.CreateDirectory(logDirectory);
		return Path.Combine(logDirectory, "Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
	}

	private string BuildRobocopyArguments(string source, string destination, string logPath)
	{
		string copyMode = IsRunningAsAdministrator() ? "/ZB" : "/Z";
		// Robocopy interpreta /R como reintentos adicionales. /R:2 limita cada
		// archivo a tres intentos totales: el inicial y dos reintentos. Si un PST
		// u otro archivo permanece bloqueado, Robocopy lo registra, conserva la
		// copia válida que ya exista en el NAS y continúa con los demás archivos.
		string arguments = $"\"{source}\" \"{destination}\" /MIR /COPY:DAT /DCOPY:DAT /R:2 /W:2 {copyMode} /FFT /FP /NDL /XJ /BYTES /ETA /TEE /LOG+:\"{logPath}\"";
		if (excluirOcultos)
		{
			arguments += " /XA:SH";
		}
		if (excluirTemporales)
		{
			arguments += " /XF \"~$*\" \"*.tmp\" \"*.temp\" \"*.partial\" \"*.crdownload\" \"*.download\"";
		}
		return arguments;
	}

	private static bool IsRunningAsAdministrator()
	{
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		WindowsPrincipal principal = new WindowsPrincipal(identity);
		return principal.IsInRole(WindowsBuiltInRole.Administrator);
	}

	private static string FormatDisplayPath(string fullPath, string profilePath)
	{
		string normalizedPath = fullPath.Trim();
		if (normalizedPath.StartsWith(profilePath, StringComparison.OrdinalIgnoreCase))
		{
			string relativePath = normalizedPath.Substring(profilePath.Length).TrimStart('\\', '/');
			if (relativePath != "")
			{
				relativePath = relativePath.Replace('\\', '/');
				int separatorIndex = relativePath.IndexOf('/');
				string rootFolder = separatorIndex >= 0 ? relativePath.Substring(0, separatorIndex) : relativePath;
				string remainder = separatorIndex >= 0 ? relativePath.Substring(separatorIndex) : "";
				string displayRoot = rootFolder.ToLowerInvariant() switch
				{
					"desktop" => "Escritorio",
					"documents" => "Documentos",
					"pictures" => "Imagenes",
					"downloads" => "Descargas",
					"music" => "Musica",
					"videos" => "Videos",
					_ => rootFolder
				};
				return displayRoot + remainder;
			}
		}
		return Path.GetFileName(normalizedPath);
	}

	private static bool IsTemporaryFileName(string path)
	{
		string fileName = Path.GetFileName(path);
		return fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) || TemporaryExtensions.Contains(Path.GetExtension(fileName));
	}

	private int CleanupDestinationTemporaryFiles(
		string destinationRoot,
		BackgroundWorker backgroundWorker)
	{
		int failures = 0;
		Stack<string> pendingDirectories = new Stack<string>();
		pendingDirectories.Push(destinationRoot);
		while (pendingDirectories.Count > 0)
		{
			if (backgroundWorker.CancellationPending)
			{
				break;
			}
			string currentDirectory = pendingDirectories.Pop();
			lastRobocopyActivityUtc = DateTime.UtcNow;
			try
			{
				foreach (string file in Directory.GetFiles(LongPath(currentDirectory)))
				{
					if (IsTemporaryFileName(file))
					{
						try
						{
							File.Delete(LongPath(file));
						}
						catch
						{
							failures++;
						}
					}
				}
				foreach (string directory in Directory.GetDirectories(LongPath(currentDirectory)))
				{
					try
					{
						if ((File.GetAttributes(LongPath(directory)) & FileAttributes.ReparsePoint) == 0)
						{
							pendingDirectories.Push(directory);
						}
					}
					catch
					{
						failures++;
					}
				}
			}
			catch
			{
				failures++;
			}
		}
		return failures;
	}

	private long CalculateEligibleSourceBytes(string sourcePath)
	{
		long totalBytes = 0;
		EnumerationOptions options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			ReturnSpecialDirectories = false,
			AttributesToSkip = FileAttributes.ReparsePoint
		};
		try
		{
			foreach (string file in Directory.EnumerateFiles(LongPath(sourcePath), "*", options))
			{
				try
				{
					FileInfo info = new FileInfo(file);
					if (excluirOcultos && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
					{
						continue;
					}
					if (excluirTemporales && IsTemporaryFileName(file))
					{
						continue;
					}
					long length = Math.Max(0, info.Length);
					totalBytes = length > long.MaxValue - totalBytes ? long.MaxValue : totalBytes + length;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return totalBytes;
	}

	private static void ReadRobocopyRecords(StreamReader reader, Action<string> handleRecord)
	{
		char[] buffer = new char[4096];
		StringBuilder record = new StringBuilder();
		int read;
		while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
		{
			for (int index = 0; index < read; index++)
			{
				char value = buffer[index];
				if (value == '\r' || value == '\n')
				{
					if (record.Length > 0)
					{
						handleRecord(record.ToString());
						record.Clear();
					}
					continue;
				}
				record.Append(value);
			}
		}
		if (record.Length > 0)
		{
			handleRecord(record.ToString());
		}
	}

	private static long GetFileLengthSafely(string path)
	{
		try
		{
			return Math.Max(0, new FileInfo(LongPath(path)).Length);
		}
		catch
		{
			return 0;
		}
	}

	private static bool TryExtractSourceFilePath(
		string output,
		string sourcePath,
		out string candidatePath)
	{
		candidatePath = "";
		int sourceIndex = output.IndexOf(sourcePath, StringComparison.OrdinalIgnoreCase);
		if (sourceIndex < 0)
		{
			return false;
		}

		string value = output.Substring(sourceIndex).Trim();
		if (File.Exists(LongPath(value)))
		{
			candidatePath = value;
			return true;
		}

		value = RobocopyEtaSuffixRegex.Replace(value, "").TrimEnd();
		if (!File.Exists(LongPath(value)))
		{
			return false;
		}
		candidatePath = value;
		return true;
	}

	private static Encoding GetRobocopyOutputEncoding()
	{
		try
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
		}
		catch
		{
			return Encoding.UTF8;
		}
	}

	private int RunRobocopy(string args, BackgroundWorker bw, string carpeta, int overallStartProgress, int overallEndProgress, string sourcePath)
	{
		runObserver?.FolderStarted(carpeta); // OBSERVE_ONLY
		currentRobocopyPhase = "Inventariando archivos locales";
		bw.ReportProgress(overallStartProgress, new BackupProgress("Analizando: " + carpeta + "...", 0, "Calculando progreso total"));
		long totalSourceBytes = CalculateEligibleSourceBytes(sourcePath);
		currentRobocopyPhase = "Comparando el origen con el NAS";
		Encoding robocopyEncoding = GetRobocopyOutputEncoding();
		using Process process = new Process
		{
			StartInfo = new ProcessStartInfo("robocopy.exe", args)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = robocopyEncoding,
				StandardErrorEncoding = robocopyEncoding
			}
		};
		string currentFile = carpeta;
		string currentSourceFile = "";
		long completedBytes = 0;
		long currentFileBytes = 0;
		int lastOverallProgress = overallStartProgress;
		string lastReportedFile = "";
		double lastReportedFileProgress = -2.0;
		double lastObservedFileProgress = -1.0;
		Action<string> handleOutput = textValue =>
		{
			string text = textValue.Trim();
			if (text == "")
			{
				return;
			}
			runObserver?.Observe(carpeta, text); // OBSERVE_ONLY
			lastRobocopyActivityUtc = DateTime.UtcNow;
			double fileProgress = -1.0;
			bool foundFile = false;
			if (TryExtractSourceFilePath(text, sourcePath, out string candidatePath))
			{
				foundFile = true;
				if (!candidatePath.Equals(currentSourceFile, StringComparison.OrdinalIgnoreCase))
				{
					completedBytes = Math.Min(totalSourceBytes, completedBytes + currentFileBytes);
					currentSourceFile = candidatePath;
					currentFileBytes = GetFileLengthSafely(candidatePath);
					lastObservedFileProgress = 0.0;
				}
				string relativeToSource = candidatePath.Substring(sourcePath.Length).TrimStart('\\', '/');
				currentFile = FormatDisplayPath(Path.Combine(userProfile, carpeta, relativeToSource), userProfile);
				if (!currentFile.Equals(currentActivityFile, StringComparison.OrdinalIgnoreCase))
				{
					currentActivityFile = currentFile;
					currentFileStartedUtc = DateTime.UtcNow;
				}
				fileProgress = 0.0;
				currentRobocopyPhase = "Copiando archivos";
			}
			Match percentMatch = RobocopyPercentRegex.Match(text);
			if (percentMatch.Success)
			{
				string normalizedPercent = percentMatch.Groups["percent"].Value.Replace(',', '.');
				if (double.TryParse(normalizedPercent, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedProgress))
				{
					fileProgress = Math.Clamp(parsedProgress, 0.0, 100.0);
					lastObservedFileProgress = fileProgress;
					currentRobocopyPhase = fileProgress >= 99.9
						? "Finalizando la transferencia del archivo"
						: "Copiando archivos";
				}
			}
			if (text.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
			{
				currentFile = text
					.Replace(sourcePath, FormatDisplayPath(Path.Combine(userProfile, carpeta), userProfile), StringComparison.OrdinalIgnoreCase)
					.Replace(userProfile, "Perfil del usuario", StringComparison.OrdinalIgnoreCase);
			}
			if (totalSourceBytes > 0)
			{
				double currentBytes = fileProgress < 0 ? 0 : (double)currentFileBytes * fileProgress / 100.0;
				double ratio = Math.Clamp(((double)completedBytes + currentBytes) / totalSourceBytes, 0.0, 1.0);
				int range = Math.Max(0, overallEndProgress - overallStartProgress);
				int calculated = overallStartProgress + (int)Math.Floor(range * ratio);
				if (overallEndProgress > overallStartProgress)
				{
					calculated = Math.Min(calculated, overallEndProgress - 1);
				}
				lastOverallProgress = Math.Max(lastOverallProgress, calculated);
			}
			bool hasError = text.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
			bool fileChanged = foundFile &&
				!currentFile.Equals(lastReportedFile, StringComparison.OrdinalIgnoreCase);
			bool progressChanged = percentMatch.Success &&
				Math.Abs(fileProgress - lastReportedFileProgress) >= 0.05;
			if (fileChanged || progressChanged || hasError)
			{
				bw.ReportProgress(lastOverallProgress, new BackupProgress("Sincronizando: " + carpeta + "...", fileProgress, currentFile));
				lastReportedFile = currentFile;
				lastReportedFileProgress = fileProgress;
			}
		};
		Action<string> handleError = errorValue =>
		{
			if (!string.IsNullOrWhiteSpace(errorValue))
			{
				string safeError = errorValue.Trim()
					.Replace(sourcePath, FormatDisplayPath(Path.Combine(userProfile, carpeta), userProfile), StringComparison.OrdinalIgnoreCase)
					.Replace(userProfile, "Perfil del usuario", StringComparison.OrdinalIgnoreCase);
				bw.ReportProgress(lastOverallProgress, new BackupProgress("Robocopy informo un problema en " + carpeta + ".", -1, safeError));
			}
		};
		if (!process.Start())
		{
			throw new InvalidOperationException("No fue posible iniciar robocopy.exe.");
		}
		lock (processLock)
		{
			activeRobocopy = process;
		}
		try
		{
			Task outputReader = Task.Run(() => ReadRobocopyRecords(process.StandardOutput, handleOutput));
			Task errorReader = Task.Run(() => ReadRobocopyRecords(process.StandardError, handleError));
			while (!process.WaitForExit(200))
			{
				if (bw.CancellationPending)
				{
					StopProcess(process);
					process.WaitForExit();
					Task.WaitAll(outputReader, errorReader);
					runObserver?.FolderFinished(carpeta, -1); // OBSERVE_ONLY
					return -1;
				}
			}
			process.WaitForExit();
			Task.WaitAll(outputReader, errorReader);
			runObserver?.FolderFinished(carpeta, bw.CancellationPending ? -1 : process.ExitCode); // OBSERVE_ONLY
			currentRobocopyPhase = "Finalizando el proceso y leyendo el resultado";
			bw.ReportProgress(
				Math.Max(overallStartProgress, overallEndProgress - 1),
				new BackupProgress(
					"Finalizando: " + carpeta + "...",
					lastObservedFileProgress < 0 ? 0 : 100,
					"Resultado de la carpeta recibido"));
			return bw.CancellationPending ? -1 : process.ExitCode;
		}
		finally
		{
			lock (processLock)
			{
				if (ReferenceEquals(activeRobocopy, process))
				{
					activeRobocopy = null;
				}
			}
		}
	}

	private static void StopProcess(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
	}

	private void StopActiveRobocopy()
	{
		lock (processLock)
		{
			if (activeRobocopy != null)
			{
				StopProcess(activeRobocopy);
			}
		}
	}

	private void BuildWorker()
	{
		worker.WorkerReportsProgress = true;
		worker.WorkerSupportsCancellation = true;
		worker.DoWork += delegate(object? s, DoWorkEventArgs e)
		{
			BackupRequest request = (BackupRequest)e.Argument!;
			currentBackupStartedAtUtc = DateTimeOffset.UtcNow;
			currentLogPath = CreateLogPath();
			runObserver?.SetLog(currentLogPath);
			BackupResult result = new BackupResult(currentLogPath);
			worker.ReportProgress(5, new BackupProgress("Preparando el acceso al NAS...", 0, "Conservando las conexiones abiertas"));
			ConnectNAS(request.User, request.Password);
			if (worker.CancellationPending)
			{
				e.Cancel = true;
				return;
			}
			worker.ReportProgress(8, new BackupProgress("Credenciales verificadas.", 0, "Acceso al destino confirmado"));
			currentSnapshotMode = "live";
			currentSnapshotWarning = null;
			result.SnapshotMode = "live";
			result.SnapshotWarning = null;
			worker.ReportProgress(
				9,
				new BackupProgress(
					"Preparando la copia de archivos...",
					0,
					"Los archivos bloqueados tendran hasta tres intentos"));
			int count = carpetas.Count;
			int num = 0;
			foreach (string carpeta in carpetas)
			{
				if (worker.CancellationPending)
				{
					e.Cancel = true;
					return;
				}
				num++;
				int startProgress = (int)(10.0 + (double)(num - 1) / (double)count * 85.0);
				int endProgress = (int)(10.0 + (double)num / (double)count * 85.0);
				string source = Path.Combine(userProfile, carpeta);
				string destination = Path.Combine(nasPath, carpeta);
				if (Directory.Exists(LongPath(source)))
				{
					Directory.CreateDirectory(LongPath(destination));
					worker.ReportProgress(startProgress, new BackupProgress("Sincronizando: " + carpeta + "...", 0, "Buscando archivos pendientes"));
					int exitCode = RunRobocopy(
						BuildRobocopyArguments(source, destination, currentLogPath),
						worker,
						carpeta,
						startProgress,
						endProgress,
						source);
					if (exitCode < 0)
					{
						e.Cancel = true;
						return;
					}
					int cleanupFailures = 0;
					if (excluirTemporales)
					{
						currentRobocopyPhase = "Limpiando temporales antiguos del destino";
						worker.ReportProgress(Math.Max(startProgress, endProgress - 1), new BackupProgress("Limpiando temporales antiguos en: " + carpeta + "...", 100, carpeta));
						cleanupFailures = CleanupDestinationTemporaryFiles(destination, worker);
						if (worker.CancellationPending)
						{
							e.Cancel = true;
							return;
						}
					}
					bool folderHasIssues = false;
					if (exitCode >= 8)
					{
						result.FailedFolders.Add(carpeta + " (codigo Robocopy " + exitCode + ")");
						folderHasIssues = true;
					}
					if (cleanupFailures > 0)
					{
						runObserver?.CleanupWarning(carpeta, cleanupFailures);
					}
					if (folderHasIssues)
					{
						worker.ReportProgress(endProgress, new BackupProgress("La carpeta " + carpeta + " quedo con archivos pendientes.", 0, "Se reintentaran en la proxima ejecucion"));
					}
					else
					{
						worker.ReportProgress(endProgress, new BackupProgress("Carpeta completada: " + carpeta, 100, carpeta));
					}
				}
				else
				{
					result.MissingFolders.Add(carpeta);
					runObserver?.FolderMissing(carpeta);
					worker.ReportProgress(endProgress, new BackupProgress("Carpeta omitida porque no existe: " + carpeta, 0, carpeta));
				}
			}
			e.Result = result;
			string finalStatus = "Proceso finalizado; preparando el resultado de la copia.";
			worker.ReportProgress(100, new BackupProgress(finalStatus, 100, "Proceso finalizado"));
		};
		worker.ProgressChanged += delegate(object? s, ProgressChangedEventArgs e)
		{
			progress.Value = Math.Clamp((double)e.ProgressPercentage, progress.Minimum, progress.Maximum);
			lblProgresoGeneralPercent.Text = e.ProgressPercentage + "%";
			if (e.UserState is BackupProgress backupProgress)
			{
				lblEstado.Text = backupProgress.Status;
				if (backupProgress.FileProgress >= 0)
				{
					progressArchivo.Value = Math.Clamp(backupProgress.FileProgress, progressArchivo.Minimum, progressArchivo.Maximum);
					lblArchivoPercent.Text = backupProgress.FileProgress.ToString("0.#", CultureInfo.CurrentCulture) + "%";
				}
				lblArchivoActual.Text = backupProgress.CurrentFile;
				if (backupProgress.Status.Contains("Conectando", StringComparison.OrdinalIgnoreCase))
				{
					SetConnectionVisual("Conectando", "Validando credenciales y sesion NAS.", "Conectando");
				}
				else if (backupProgress.Status.Contains("Sincronizando", StringComparison.OrdinalIgnoreCase) || backupProgress.Status.Contains("Limpiando", StringComparison.OrdinalIgnoreCase))
				{
					SetConnectionVisual("Conectado", "Conexion establecida con el NAS.", "Copiando");
				}
				else if (backupProgress.Status.Contains("Conectado", StringComparison.OrdinalIgnoreCase))
				{
					SetConnectionVisual("Conectado", "Conexion establecida con el NAS.", "Listo");
				}
				else if (backupProgress.Status.Contains("completado", StringComparison.OrdinalIgnoreCase))
				{
					SetConnectionVisual("Completado", "La ejecucion finalizo correctamente.", "Finalizado");
				}
			}
		};
		worker.RunWorkerCompleted += delegate(object? s, RunWorkerCompletedEventArgs e)
		{
			BackupRunReport? finalReport = runObserver?.Finish(e.Cancelled, e.Error);
			DisconnectNAS();
			// Detiene el indicador antes de abrir cualquier mensaje modal. De este
			// modo la interfaz no puede mostrar "Completado" y "proceso activo" al
			// mismo tiempo mientras el usuario lee el resultado.
			isRunning = false;
			progress.IsActive = false;
			progressArchivo.IsActive = false;
			currentRobocopyPhase = "";
			currentActivityFile = "";
			currentFileStartedUtc = default;
			lastRobocopyActivityUtc = default;
			lblActividad.Text = "Proceso finalizado";
			if (e.Cancelled)
			{
				lblEstado.Text = "Estado: Backup cancelado.";
				progress.Value = 0;
				progressArchivo.Value = 0;
				lblArchivoActual.Text = "Ninguno";
				SetConnectionVisual("Conectado", "Servidor NAS disponible.", "Listo");
				ResetForm();
			}
			else if (e.Error != null)
			{
				lblError.Text = e.Error.Message;
				lblEstado.Text = "Estado: Error durante el backup.";
				progress.Value = 0;
				progressArchivo.Value = 0;
				SetConnectionVisual("Atencion", "Revise el mensaje y el registro de la ejecucion.", "Error");
				string logInfo = currentLogPath == "" ? "" : "\n\nRegistro:\n" + currentLogPath;
				MessageBox.Show("Ocurrio un error:\n\n" + e.Error.Message + logInfo, "Backup NAS", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				ResetForm();
			}
			else
			{
                string message = runObserver?.UserSummary() ?? "Resultado pendiente de confirmación. No se recibió un resumen suficiente para confirmar la copia.";
                using (var resultDialog = new BackupResultDialog(finalReport, message))
                    resultDialog.ShowDialog(this);
				ResetForm();
				Close();
			}
		};
	}

    private BackupRunObserver CreateRunObserver() => new(carpetas,
        typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        nasIP, nasShare, nasPath, excluirOcultos, excluirTemporales, currentSnapshotMode, currentSnapshotWarning);

	private void ResetForm()
	{
		txtUser.Enabled = true;
		txtPass.Enabled = true;
		btnRevealPassword.Enabled = true;
		btnIniciar.Enabled = true;
		btnCancelar.Enabled = true;
		btnCancelar.Text = "Cerrar";
		progress.IsActive = false;
		progressArchivo.IsActive = false;
		lblActividad.Text = "Esperando inicio.";
		lblProgresoGeneralPercent.Text = "0%";
		lblArchivoPercent.Text = "0%";
		currentActivityFile = "";
		currentFileStartedUtc = default;
		lastRobocopyActivityUtc = default;
		currentRobocopyPhase = "";
		currentSnapshotMode = "live";
		currentSnapshotWarning = null;
		isRunning = false;
		SetConnectionVisual("Conectado", "Servidor NAS disponible.", "Listo");
		RefreshPendingUpdateNotice(force: true);
	}

	private static string GetPendingUpdateMarkerPath()
	{
		string configuredPath = Environment.GetEnvironmentVariable(
			"ATLAS_UPDATE_PENDING_FILE") ?? "";
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			try
			{
				return Path.GetFullPath(configuredPath);
			}
			catch
			{
			}
		}

		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
			"ATLAS",
			"Updater",
			"Status",
			"pending-update.json");
	}

	private static bool HasValidPendingUpdateMarker()
	{
		try
		{
			using FileStream stream = new FileStream(
				PendingUpdateMarkerPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length <= 0 || stream.Length > 64 * 1024)
			{
				return false;
			}

			using JsonDocument document = JsonDocument.Parse(stream);
			JsonElement root = document.RootElement;
			return root.ValueKind == JsonValueKind.Object &&
				root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) &&
				schemaVersion.TryGetInt32(out int schema) &&
				schema == 1 &&
				root.TryGetProperty("releaseVersion", out JsonElement releaseVersion) &&
				releaseVersion.ValueKind == JsonValueKind.String &&
				!string.IsNullOrWhiteSpace(releaseVersion.GetString());
		}
		catch
		{
			return false;
		}
	}

	private void RefreshPendingUpdateNotice(bool force = false)
	{
		if (isRunning)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		if (!force &&
			lastPendingUpdateCheckUtc != default &&
			now - lastPendingUpdateCheckUtc < TimeSpan.FromSeconds(5))
		{
			return;
		}
		lastPendingUpdateCheckUtc = now;

		bool hasPendingUpdate = HasValidPendingUpdateMarker();
		if (hasPendingUpdate)
		{
			lblActividad.Text = "Actualizacion de ATLAS lista - cerrando BackupNAS de forma segura.";
			MineralTheme.Role(lblActividad, MineralRole.Warning);
			pendingUpdateNoticeVisible = true;
			if (!closingForPendingUpdate)
			{
				closingForPendingUpdate = true;
				BeginInvoke((Action)(() =>
				{
					if (isRunning || IsDisposed)
					{
						closingForPendingUpdate = false;
						return;
					}
					DisconnectNAS();
					Close();
				}));
			}
		}
		else if (pendingUpdateNoticeVisible)
		{
			lblActividad.Text = "Esperando inicio.";
			MineralTheme.Role(lblActividad, MineralRole.Accent);
			pendingUpdateNoticeVisible = false;
		}
	}

	private void SetConnectionVisual(string title, string description, string badge)
	{
		lblStatusTitle.Text = title;
		lblStatusDescription.Text = description;
		lblStatusBadge.Text = badge;
        MineralRole tone;
        MineralRole badgeRole;
        if (badge.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            tone = MineralRole.Error; badgeRole = MineralRole.ErrorBadge; lblStatusIcon.Text = "!";
        }
        else if (badge.Equals("Conectando", StringComparison.OrdinalIgnoreCase) || badge.Equals("Cancelando", StringComparison.OrdinalIgnoreCase))
        {
            tone = MineralRole.Warning; badgeRole = MineralRole.WarningBadge; lblStatusIcon.Text = "…";
        }
        else
        {
            tone = MineralRole.Success; badgeRole = MineralRole.SuccessBadge; lblStatusIcon.Text = "✓";
        }
        MineralTheme.Role(lblStatusIcon, tone);
        MineralTheme.Role(lblStatusBadge, badgeRole);
	}

	private void PaintBrandIcon(object? sender, PaintEventArgs e)
	{
		e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		using Pen shieldPen = new Pen(MineralTheme.Palette.Accent, 2.4f);
		Point[] shield =
		{
			new Point(26, 3),
			new Point(45, 10),
			new Point(43, 34),
			new Point(26, 49),
			new Point(9, 34),
			new Point(7, 10)
		};
		e.Graphics.DrawPolygon(shieldPen, shield);
		e.Graphics.DrawLine(shieldPen, 17, 18, 35, 18);
		e.Graphics.DrawLine(shieldPen, 17, 25, 35, 25);
		e.Graphics.DrawLine(shieldPen, 17, 32, 29, 32);
	}

	private void ActivityTimer_Tick(object? sender, EventArgs e)
	{
		progress.IsActive = isRunning;
		progressArchivo.IsActive = isRunning;
		if (!isRunning)
		{
			RefreshPendingUpdateNotice();
			return;
		}
		if (currentActivityFile == "" || currentFileStartedUtc == default)
		{
			lblActividad.Text = string.IsNullOrWhiteSpace(currentRobocopyPhase)
				? "Preparando el respaldo • proceso activo"
				: currentRobocopyPhase + " • proceso activo";
			return;
		}
		TimeSpan elapsed = DateTime.UtcNow - currentFileStartedUtc;
		TimeSpan withoutOutput = lastRobocopyActivityUtc == default ? TimeSpan.Zero : DateTime.UtcNow - lastRobocopyActivityUtc;
		string elapsedText = elapsed.ToString(elapsed.TotalHours >= 1.0 ? @"hh\:mm\:ss" : @"mm\:ss");
		if (withoutOutput.TotalSeconds >= 8.0 &&
			(currentRobocopyPhase.Contains("Finalizando", StringComparison.OrdinalIgnoreCase) ||
			 currentRobocopyPhase.Contains("Limpiando", StringComparison.OrdinalIgnoreCase)))
		{
			lblActividad.Text = currentRobocopyPhase + " • proceso activo • " + elapsedText;
		}
		else if (withoutOutput.TotalSeconds >= 8.0)
		{
			lblActividad.Text = "Procesando archivo grande o respuesta del NAS • sigue activo • " + elapsedText;
		}
		else
		{
			lblActividad.Text = (string.IsNullOrWhiteSpace(currentRobocopyPhase)
				? "Copiando"
				: currentRobocopyPhase) + " • proceso activo • " + elapsedText;
		}
	}

	internal void FitToWorkingArea(Rectangle workingArea)
	{
		if (workingArea.Width <= 0 || workingArea.Height <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
		{
			return;
		}
		const int outerMargin = 16;
		int frameWidth = Math.Max(0, Width - ClientSize.Width);
		int frameHeight = Math.Max(0, Height - ClientSize.Height);
		int availableWidth = Math.Max(1, workingArea.Width - outerMargin * 2 - frameWidth);
		int availableHeight = Math.Max(1, workingArea.Height - outerMargin * 2 - frameHeight);
		float scale = Math.Min(1f, Math.Min(availableWidth / (float)ClientSize.Width, availableHeight / (float)ClientSize.Height));
		if (scale < 0.995f)
		{
			scale = Math.Max(0.7f, scale);
			SuspendLayout();
			ScaleControlTree(this, scale);
			ClientSize = new Size(
				Math.Max(1, (int)Math.Round(ClientSize.Width * scale)),
				Math.Max(1, (int)Math.Round(ClientSize.Height * scale)));
			ResumeLayout(performLayout: true);
		}
		StartPosition = FormStartPosition.Manual;
		Location = new Point(
			workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
			workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
	}

	private static void ScaleControlTree(Control parent, float scale)
	{
		foreach (Control control in parent.Controls)
		{
			Rectangle bounds = control.Bounds;
			control.Bounds = new Rectangle(
				(int)Math.Round(bounds.X * scale),
				(int)Math.Round(bounds.Y * scale),
				Math.Max(1, (int)Math.Round(bounds.Width * scale)),
				Math.Max(1, (int)Math.Round(bounds.Height * scale)));
			if (control is Label || control is TextBox || control is Button)
			{
				float fontSize = Math.Max(7f, control.Font.SizeInPoints * scale);
				control.Font = new Font(control.Font.FontFamily, fontSize, control.Font.Style, GraphicsUnit.Point, control.Font.GdiCharSet);
			}
			if (control is RoundedPanel roundedPanel)
			{
				roundedPanel.CornerRadius = Math.Max(4, (int)Math.Round(roundedPanel.CornerRadius * scale));
			}
			else if (control is ModernButton modernButton)
			{
				modernButton.CornerRadius = Math.Max(6, (int)Math.Round(modernButton.CornerRadius * scale));
			}
			else if (control is PasswordRevealButton revealButton)
			{
				revealButton.CornerRadius = Math.Max(4, (int)Math.Round(revealButton.CornerRadius * scale));
			}
			ScaleControlTree(control, scale);
		}
	}

	private void UpdatePasswordVisibility()
	{
		txtPass.PasswordChar = btnRevealPassword.IsRevealing && txtPass.Enabled ? '\0' : '●';
	}

	private void BuildUI()
	{
		SuspendLayout();
		Text = "ATLAS Backup · Copia de seguridad";
		base.ShowIcon = true;
		base.ShowInTaskbar = true;
		AutoScaleDimensions = new SizeF(96f, 96f);
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(620, 710);
		base.StartPosition = FormStartPosition.CenterScreen;
		base.FormBorderStyle = FormBorderStyle.FixedSingle;
		base.MaximizeBox = false;
		base.MinimizeBox = true;
		Font = new Font("Segoe UI", 9f);
		BackColor = Color.FromArgb(247, 250, 251);

		pnlHeader.Size = new Size(550, 82);
		pnlHeader.Location = new Point(35, 22);
		pnlHeader.BackColor = BackColor;
		base.Controls.Add(pnlHeader);

		Panel brandIcon = new Panel
		{
			Size = new Size(52, 52),
			Location = new Point(0, 13),
			BackColor = BackColor
		};
		brandIcon.Paint += PaintBrandIcon;
		pnlHeader.Controls.Add(brandIcon);

		lblTitulo.Text = "ATLAS BACKUP";
		lblTitulo.ForeColor = Color.FromArgb(23, 32, 51);
		lblTitulo.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
		lblTitulo.AutoSize = true;
		lblTitulo.Location = new Point(70, 12);
		pnlHeader.Controls.Add(lblTitulo);

		lblSubtit.Text = "ATLAS - Departamento de Ciberseguridad";
		lblSubtit.ForeColor = Color.FromArgb(0, 145, 170);
		lblSubtit.Font = new Font("Segoe UI Semibold", 9f);
		lblSubtit.AutoSize = true;
		lblSubtit.Location = new Point(72, 50);
		pnlHeader.Controls.Add(lblSubtit);

		RoundedPanel credentialsCard = new RoundedPanel
		{
			Size = new Size(550, 190),
			Location = new Point(35, 112),
			BackColor = Color.White,
			CornerRadius = 15,
			BorderColor = Color.FromArgb(226, 232, 236)
		};
		base.Controls.Add(credentialsCard);

		lblUser.Text = "Usuario NAS";
		lblUser.Font = new Font("Segoe UI Semibold", 9f);
		lblUser.ForeColor = Color.FromArgb(36, 46, 62);
		lblUser.AutoSize = true;
		lblUser.Location = new Point(24, 27);
		credentialsCard.Controls.Add(lblUser);

		RoundedPanel userInputPanel = new RoundedPanel
		{
			Size = new Size(350, 38),
			Location = new Point(174, 16),
			BackColor = Color.FromArgb(252, 253, 253),
			CornerRadius = 9,
			BorderColor = Color.FromArgb(218, 228, 232)
		};
		credentialsCard.Controls.Add(userInputPanel);

		txtUser.Location = new Point(12, 8);
		txtUser.Size = new Size(326, 24);
		txtUser.Font = new Font("Segoe UI", 10f);
		txtUser.BorderStyle = BorderStyle.None;
		txtUser.BackColor = Color.FromArgb(252, 253, 253);
		userInputPanel.Controls.Add(txtUser);

		lblPass.Text = "Contraseña NAS";
		lblPass.Font = new Font("Segoe UI Semibold", 9f);
		lblPass.ForeColor = Color.FromArgb(36, 46, 62);
		lblPass.AutoSize = true;
		lblPass.Location = new Point(24, 71);
		credentialsCard.Controls.Add(lblPass);

		RoundedPanel passwordInputPanel = new RoundedPanel
		{
			Size = new Size(350, 38),
			Location = new Point(174, 60),
			BackColor = Color.FromArgb(252, 253, 253),
			CornerRadius = 9,
			BorderColor = Color.FromArgb(218, 228, 232)
		};
		credentialsCard.Controls.Add(passwordInputPanel);

		txtPass.Location = new Point(12, 8);
		txtPass.Size = new Size(292, 24);
		txtPass.PasswordChar = '●';
		txtPass.Font = new Font("Segoe UI", 10f);
		txtPass.BorderStyle = BorderStyle.None;
		txtPass.BackColor = Color.FromArgb(252, 253, 253);
		passwordInputPanel.Controls.Add(txtPass);

		btnRevealPassword.Location = new Point(310, 4);
		btnRevealPassword.Size = new Size(34, 30);
		btnRevealPassword.RevealChanged += delegate
		{
			UpdatePasswordVisibility();
		};
		passwordInputPanel.Controls.Add(btnRevealPassword);

		RoundedPanel connectionPanel = new RoundedPanel
		{
			Size = new Size(520, 66),
			Location = new Point(15, 111),
			BackColor = Color.FromArgb(242, 250, 250),
			CornerRadius = 12,
			BorderColor = Color.FromArgb(221, 239, 240)
		};
		credentialsCard.Controls.Add(connectionPanel);

		lblStatusIcon.AutoSize = false;
		lblStatusIcon.Size = new Size(34, 34);
		lblStatusIcon.Location = new Point(15, 15);
		lblStatusIcon.Font = new Font("Segoe UI Semibold", 17f);
		lblStatusIcon.TextAlign = ContentAlignment.MiddleCenter;
		connectionPanel.Controls.Add(lblStatusIcon);

		lblStatusTitle.AutoSize = true;
		lblStatusTitle.Location = new Point(56, 11);
		lblStatusTitle.Font = new Font("Segoe UI Semibold", 9.5f);
		lblStatusTitle.ForeColor = Color.FromArgb(31, 46, 58);
		connectionPanel.Controls.Add(lblStatusTitle);

		lblStatusDescription.AutoSize = true;
		lblStatusDescription.Location = new Point(56, 35);
		lblStatusDescription.Font = new Font("Segoe UI", 8f);
		lblStatusDescription.ForeColor = Color.FromArgb(91, 107, 117);
		connectionPanel.Controls.Add(lblStatusDescription);

		lblStatusBadge.AutoSize = false;
		lblStatusBadge.Size = new Size(88, 28);
		lblStatusBadge.Location = new Point(414, 19);
		lblStatusBadge.Font = new Font("Segoe UI Semibold", 8f);
		lblStatusBadge.TextAlign = ContentAlignment.MiddleCenter;
		connectionPanel.Controls.Add(lblStatusBadge);
		SetConnectionVisual("Conectado", "Servidor NAS disponible.", "Listo");

		RoundedPanel progressCard = new RoundedPanel
		{
			Size = new Size(550, 250),
			Location = new Point(35, 320),
			BackColor = Color.White,
			CornerRadius = 15,
			BorderColor = Color.FromArgb(226, 232, 236)
		};
		base.Controls.Add(progressCard);

		lblEstado.Text = "Esperando inicio...";
		lblEstado.Font = new Font("Segoe UI", 8.5f);
		lblEstado.ForeColor = Color.FromArgb(91, 107, 117);
		lblEstado.AutoSize = false;
		lblEstado.AutoEllipsis = true;
		lblEstado.Size = new Size(500, 20);
		lblEstado.Location = new Point(25, 18);
		progressCard.Controls.Add(lblEstado);

		lblProgresoGeneral.Text = "Progreso general";
		lblProgresoGeneral.Font = new Font("Segoe UI Semibold", 9.5f);
		lblProgresoGeneral.ForeColor = Color.FromArgb(31, 46, 58);
		lblProgresoGeneral.AutoSize = true;
		lblProgresoGeneral.Location = new Point(25, 48);
		progressCard.Controls.Add(lblProgresoGeneral);

		lblProgresoGeneralPercent.Text = "0%";
		lblProgresoGeneralPercent.Font = new Font("Segoe UI Semibold", 9.5f);
		lblProgresoGeneralPercent.ForeColor = Color.FromArgb(31, 46, 58);
		lblProgresoGeneralPercent.AutoSize = false;
		lblProgresoGeneralPercent.TextAlign = ContentAlignment.MiddleRight;
		lblProgresoGeneralPercent.Size = new Size(60, 20);
		lblProgresoGeneralPercent.Location = new Point(465, 46);
		progressCard.Controls.Add(lblProgresoGeneralPercent);

		progress.Location = new Point(25, 73);
		progress.Size = new Size(500, 17);
		progress.Minimum = 0;
		progress.Maximum = 100;
		progressCard.Controls.Add(progress);

		Label currentFileTitle = new Label
		{
			Text = "Archivo actual",
			Font = new Font("Segoe UI Semibold", 9.5f),
			ForeColor = Color.FromArgb(31, 46, 58),
			AutoSize = true,
			Location = new Point(25, 111)
		};
		progressCard.Controls.Add(currentFileTitle);

		lblArchivoPercent.Text = "0%";
		lblArchivoPercent.Font = new Font("Segoe UI Semibold", 9.5f);
		lblArchivoPercent.ForeColor = Color.FromArgb(31, 46, 58);
		lblArchivoPercent.AutoSize = false;
		lblArchivoPercent.TextAlign = ContentAlignment.MiddleRight;
		lblArchivoPercent.Size = new Size(60, 20);
		lblArchivoPercent.Location = new Point(465, 109);
		progressCard.Controls.Add(lblArchivoPercent);

		progressArchivo.Location = new Point(25, 136);
		progressArchivo.Size = new Size(500, 17);
		progressArchivo.Minimum = 0;
		progressArchivo.Maximum = 100;
		progressCard.Controls.Add(progressArchivo);

		lblArchivoActual.Text = "Ninguno";
		lblArchivoActual.Font = new Font("Segoe UI", 8.5f);
		lblArchivoActual.ForeColor = Color.FromArgb(88, 104, 115);
		lblArchivoActual.AutoSize = false;
		lblArchivoActual.AutoEllipsis = true;
		lblArchivoActual.Size = new Size(500, 20);
		lblArchivoActual.Location = new Point(25, 164);
		progressCard.Controls.Add(lblArchivoActual);

		lblActividad.Text = "Esperando inicio.";
		lblActividad.Font = new Font("Segoe UI", 8f);
		MineralTheme.Role(lblActividad, MineralRole.Accent);
		lblActividad.AutoSize = false;
		lblActividad.AutoEllipsis = true;
		lblActividad.Size = new Size(500, 22);
		lblActividad.Location = new Point(25, 197);
		progressCard.Controls.Add(lblActividad);

		lblError.ForeColor = Color.FromArgb(194, 57, 57);
		lblError.Font = new Font("Segoe UI", 8f);
		lblError.AutoSize = false;
		lblError.AutoEllipsis = true;
		lblError.Size = new Size(520, 36);
		lblError.Location = new Point(50, 578);
		base.Controls.Add(lblError);

		btnIniciar.Text = "▶  Iniciar backup";
		btnIniciar.Location = new Point(82, 622);
		btnIniciar.Size = new Size(220, 44);



		btnIniciar.Click += BtnIniciar_Click;
		base.Controls.Add(btnIniciar);

		btnCancelar.Text = "Cerrar";
		btnCancelar.Location = new Point(318, 622);
		btnCancelar.Size = new Size(180, 44);




		btnCancelar.Click += BtnCancelar_Click;
		base.Controls.Add(btnCancelar);

		lblVersion.Text = $"Versión {typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} · Copia de seguridad";
		lblVersion.ForeColor = Color.FromArgb(130, 143, 151);
		lblVersion.Font = new Font("Segoe UI", 8f);
		lblVersion.AutoSize = true;
		lblVersion.Location = new Point(35, 682);
		base.Controls.Add(lblVersion);

		activityTimer.Interval = 1000;
		activityTimer.Tick += ActivityTimer_Tick;
		activityTimer.Tick += (_, _) => { if (isRunning) runObserver?.Heartbeat(); };
		if (!previewMode) activityTimer.Start();
		base.AcceptButton = btnIniciar;
		base.Shown += delegate
		{
			FitToWorkingArea(Screen.FromControl(this).WorkingArea);
			if (!previewMode) RefreshPendingUpdateNotice(force: true);
		};
		base.DpiChanged += delegate
		{
			BeginInvoke(delegate
			{
				FitToWorkingArea(Screen.FromControl(this).WorkingArea);
			});
		};
		base.FormClosing += MainForm_FormClosing;
		base.FormClosed += delegate
		{
			activityTimer.Stop();
		};
        MineralThemeButton appearance = new() { Location = new Point(415, 12), Size = new Size(132, 36) };
        pnlHeader.Controls.Add(appearance);
        foreach (Control card in new Control[] { credentialsCard, progressCard }) MineralTheme.Role(card, MineralRole.Surface);
        foreach (Control field in new Control[] { userInputPanel, passwordInputPanel, connectionPanel, txtUser, txtPass }) MineralTheme.Role(field, MineralRole.Soft);
        foreach (Control label in new Control[] { lblEstado, lblStatusDescription, lblArchivoActual, lblVersion }) MineralTheme.Role(label, MineralRole.Muted);
        MineralTheme.Role(lblSubtit, MineralRole.Accent);
        MineralTheme.Role(lblError, MineralRole.Error);
        MineralTheme.Role(btnIniciar, MineralRole.PrimaryButton);
        MineralTheme.Attach(this);
        ResumeLayout(performLayout: false);
	}

	private void BtnIniciar_Click(object? sender, EventArgs e)
	{
		if (!isRunning)
		{
			lblError.Text = "";
			string text = txtUser.Text.Trim();
			string text2 = txtPass.Text;
			if (text == "" || text2 == "")
			{
				lblError.Text = "Por favor ingrese usuario y contrasena.";
				return;
			}
			isRunning = true;
			txtUser.Enabled = false;
			txtPass.Enabled = false;
			btnRevealPassword.Enabled = false;
			btnIniciar.Enabled = false;
			btnCancelar.Enabled = true;
			btnCancelar.Text = "Cancelar";
			progress.Value = 0;
			progressArchivo.Value = 0;
			progress.IsActive = true;
			progressArchivo.IsActive = true;
			lblProgresoGeneralPercent.Text = "0%";
			lblArchivoPercent.Text = "0%";
			lblArchivoActual.Text = "Preparando...";
			lblActividad.Text = "Preparando el respaldo • proceso activo";
			currentActivityFile = "";
			currentFileStartedUtc = DateTime.UtcNow;
			lastRobocopyActivityUtc = DateTime.UtcNow;
			currentRobocopyPhase = "Preparando autenticacion SMB";
			SetConnectionVisual("Conectando", "Validando credenciales y sesion NAS.", "Conectando");
			currentLogPath = "";
			runObserver = CreateRunObserver();
			runObserver.Start();
			BackupRequest request = new BackupRequest(text, text2);
			txtPass.Clear();
			worker.RunWorkerAsync(request);
		}
	}

	private void RequestCancellation()
	{
		if (!isRunning)
		{
			return;
		}
		worker.CancelAsync();
		btnCancelar.Enabled = false;
		btnCancelar.Text = "Cancelando...";
		lblEstado.Text = "Cancelando de forma segura...";
		SetConnectionVisual("Conectado", "Cerrando el proceso de copia de forma segura.", "Cancelando");
		StopActiveRobocopy();
	}

	private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
	{
		if (isRunning)
		{
			e.Cancel = true;
			RequestCancellation();
		}
		else if (!previewMode && runObserver is null && !closingForPendingUpdate)
		{
			runObserver = CreateRunObserver();
			runObserver.Finish(skipped: true);
		}
	}

	private void BtnCancelar_Click(object? sender, EventArgs e)
	{
		if (isRunning)
		{
			RequestCancellation();
			return;
		}
		DisconnectNAS();
		Close();
	}
}
