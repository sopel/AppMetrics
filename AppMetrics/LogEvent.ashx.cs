﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using System.Web;
using Timer = System.Timers.Timer;

namespace AppMetrics
{
	/// <summary>
	/// Summary description for LogEvent
	/// </summary>
	public class LogEvent : IHttpHandler
	{
		public static void Init()
		{
			lock (Sync)
			{
				if (_logFile == null)
				{
					var logPath = Path.Combine(AppSettings.AppDataPath, Const.LogFileName);
					_logFile = new StreamWriter(logPath, true, Encoding.UTF8) { AutoFlush = true };
				}
				if (_timer == null)
				{
					_timer = new Timer { Interval = 1000 * 60 * 15, AutoReset = false };
					_timer.Elapsed += OnTimer;

					ThreadPool.QueueUserWorkItem(
						s =>
						{
							OnTimer(null, null);
							_timer.Start();
						});
				}
			}
		}

		public void ProcessRequest(HttpContext context)
		{
			try
			{
				Init();

				var applicationKey = context.Request.Params["MessageAppKey"];
				if (string.IsNullOrEmpty(applicationKey))
					throw new ApplicationException("No application key");

				var messages = context.Request.Params["MessagesList"];
				if (!string.IsNullOrEmpty(messages))
					ProcessMessages(context.Request, applicationKey, messages);
				else
					ProcessMessage(context, applicationKey);
			}
			catch (Exception exc)
			{
				ReportLog(exc);
#if DEBUG
				context.Response.Write(exc);
#endif
			}
		}

		#region Multi-message mode

		private static void ProcessMessages(HttpRequest request, string applicationKey, string messagesText)
		{
			var textLines = messagesText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			var itemsByLines = textLines.Select(line => line.Split('\t')).ToArray();
			var tmp = itemsByLines.GroupBy(line => line[0], line => line.Skip(1).ToArray()).
				ToDictionary(group => group.Key, group => group.ToArray());
			var messagesBySessions = new Dictionary<string, string[][]>(tmp);

			foreach (var pair in messagesBySessions)
			{
				var sessionId = pair.Key;
				var lines = pair.Value;
				if (lines.Count() == 0)
					continue;

				if (lines.Any(line => line.Length != 3))
					throw new ApplicationException("Invalid count of items in the line");
				WriteData(request, applicationKey, sessionId, lines);
			}
		}

		private static void WriteData(HttpRequest request, string appKey, string sessionId, string[][] lines)
		{
			var filePath = GetDataFilePath(appKey, sessionId);
			using (var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
			{
				using (var writer = new StreamWriter(stream)) // by default, encoding is UTF8 without BOM
				{
					var fileExisted = writer.BaseStream.Length > 0;
					if (fileExisted)
					{
						writer.BaseStream.Seek(0, SeekOrigin.End);
					}
					else
					{
						writer.BaseStream.Write(Const.Utf8Bom, 0, Const.Utf8Bom.Length);
						var clientTime = lines[0][0];

						writer.WriteLine("{0}\t{1}\t{2}", clientTime, "ClientIP", request.UserHostAddress);
						writer.WriteLine("{0}\t{1}\t{2}", clientTime, "ClientHostName", request.UserHostName);
						writer.WriteLine("{0}\t{1}\t{2}", clientTime, "ClientUserAgent", request.UserAgent);
					}

					foreach (var item in lines)
					{
						var line = string.Join("\t", item);
						writer.WriteLine(line);
					}
				}
			}
		}

		#endregion

		#region Single-message mode

		private static void ProcessMessage(HttpContext context, string applicationKey)
		{
			var sessionId = context.Request.Params["MessageSession"];
			if (string.IsNullOrEmpty(sessionId))
				throw new ApplicationException("No session ID");

			var filePath = GetDataFilePath(applicationKey, sessionId);

			using (var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
			{
				using (var writer = new StreamWriter(stream)) // by default, encoding is Encoding.UTF8 without BOM
				{
					WriteData(writer, context);
				}
			}
		}

		private static void WriteData(StreamWriter writer, HttpContext context)
		{
			var fileExisted = writer.BaseStream.Length > 0;
			writer.BaseStream.Seek(0, SeekOrigin.End);

			var name = context.Request.Params["MessageName"];
			var data = context.Request.Params["MessageData"];
			var clientTime = context.Request.Params["MessageTime"];

			if (!fileExisted)
			{
				writer.BaseStream.Write(Const.Utf8Bom, 0, Const.Utf8Bom.Length);

				writer.WriteLine("{0}\t{1}\t{2}", clientTime, "ClientIP", context.Request.UserHostAddress);
				writer.WriteLine("{0}\t{1}\t{2}", clientTime, "ClientHostName", context.Request.UserHostName);
				writer.WriteLine("{0}\t{1}\t{2}", clientTime, "ClientUserAgent", context.Request.UserAgent);
			}

			data = Util.Escape(data);
			writer.WriteLine("{0}\t{1}\t{2}", clientTime, name, data);
		}

		#endregion

		private static string GetDataFilePath(string applicationKey, string sessionId)
		{
			var basePath = AppSettings.DataStoragePath;
			var dataRootPath = Path.Combine(basePath, applicationKey);
			if (!dataRootPath.StartsWith(basePath)) // block malicious application keys
				throw new ArgumentException(dataRootPath);

			if (!Directory.Exists(dataRootPath))
				Directory.CreateDirectory(dataRootPath);

			var filesMask = string.Format("*.{0}.txt", sessionId);
			var files = Directory.GetFiles(dataRootPath, filesMask);
			if (files.Length > 0)
			{
				if (files.Length != 1)
					throw new ApplicationException(string.Format("Too much data files for session {0}", sessionId));
				return files[0];
			}
			else
			{
				var time = DateTime.UtcNow.ToString("yyyy-MM-dd HH_mm_ss");
				var filePath = Path.GetFullPath(string.Format("{0}\\{1}.{2}.txt", dataRootPath, time, sessionId));
				if (!filePath.StartsWith(dataRootPath)) // block malicious session ids
					throw new ArgumentException(filePath);
				return filePath;
			}
		}

		static void ReportLog(object val, LogPriority priority = LogPriority.High)
		{
			try
			{
				var text = val.ToString();

				if (priority != LogPriority.Low)
					EventLog.WriteEntry(Const.EventLogSourceName, text);

				if (_logFile != null)
				{
					var time = DateTime.UtcNow;
					bool multiLineData = text.Contains('\n');
					if (multiLineData)
					{
						_logFile.WriteLine(time);
						_logFile.WriteLine(Const.Delimiter);
						_logFile.WriteLine(text);
						_logFile.WriteLine(Const.Delimiter);
					}
					else
					{
						_logFile.WriteLine("{0}\t{1}", time, text);
					}
				}
			}
			catch (Exception exc)
			{
				Trace.WriteLine(val);
				Trace.WriteLine(exc);
			}
		}

		static void OnTimer(object sender, ElapsedEventArgs e)
		{
			using (var mutex = new Mutex(false, "AppMetrics.Backup"))
			{
				if (!mutex.WaitOne(TimeSpan.FromSeconds(3), false))
					return;

				Backup.BackupAll(ReportLog);
			}
			_timer.Enabled = true;
		}

		private static Timer _timer;

		private static StreamWriter _logFile;
		static readonly object Sync = new object();

		public bool IsReusable
		{
			get
			{
				return true;
			}
		}
	}

	public enum LogPriority { Low, High }
	public delegate void ReportLogDelegate(object val, LogPriority priority = LogPriority.High);
}