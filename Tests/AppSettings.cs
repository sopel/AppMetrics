﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace Tests
{
	public class AppSettings
	{
		public AppSettings()
		{
			SetDefaults();
		}

		public string ServiceRootUrl { get; set; }
		public string UserName { get; set; }
		public string Password { get; set; }

		[XmlIgnore]
		public string MetricsLoggingUrl { get { return CombineUri(ServiceRootUrl, "LogEvent.ashx"); } }
		[XmlIgnore]
		public string MetricsExportUrl { get { return CombineUri(ServiceRootUrl, "DataService.svc/"); } }

		public static string CombineUri(string root, string tail)
		{
			var res = new Uri(new Uri(root), tail);
			return res.AbsoluteUri;
		}

		#region Config storing implementation

		private static AppSettings _instance;

		public static AppSettings Instance
		{
			get { return _instance ?? (_instance = Load()); }
		}

		private static readonly string FileName = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
			+ @"\City Index\AppMetrics.Tests\AppSettings.xml";

		public static void Reload()
		{
			_instance = Load();
		}

		public static AppSettings Load()
		{
			AppSettings settings;

			if (File.Exists(FileName))
			{
				var s = new XmlSerializer(typeof(AppSettings));
				using (var rd = new StreamReader(FileName))
				{
					settings = (AppSettings)s.Deserialize(rd);
				}
			}
			else
				settings = new AppSettings();

			return settings;
		}

		public void Save()
		{
			var directory = Path.GetDirectoryName(FileName);
			if (!Directory.Exists(directory))
				Directory.CreateDirectory(directory);

			var s = new XmlSerializer(typeof(AppSettings));
			using (var writer = new StreamWriter(FileName))
			{
				s.Serialize(writer, this);
			}
		}

		private void SetDefaults()
		{
		}

		#endregion
	}
}