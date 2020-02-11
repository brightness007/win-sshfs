﻿using CommandLine;
using DokanNet;
using DokanNet.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sshfs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SSHFS.CLI
{
    class Options
    {
        // Paths
        [Option('d', "drive-letter",
            Required = true,
            HelpText = "Drive letter to mount the remote SFTP path under")]
        public char DriveLetter { get; set; }

        [Option('r', "path",
            Required = true,
            HelpText = "Absolute path of directory to be mounted from remote system")]
        public string Path { get; set; }

        // Remote host
        [Option('h', "host",
            Required = true,
            HelpText = "IP or hostname of remote host")]
        public string Host { get; set; }

        [Option('p', "port",
            Required = false, Default = 22,
            HelpText = "SSH service port on remote server")]
        public int Port { get; set; }

        // Auth
        [Option('u', "username",
            Required = true,
            HelpText = "Name of SSH user on remote system")]
        public string Username { get; set; }

        [Option('x', "password",
            Required = false,
            HelpText = "SSH user Password")]
        public string Password { get; set; }

        [Option('k', "private-keys",
            Required = false,
            HelpText = "Path to SSH user's private key(s), if key-based auth should be attempted")]
        public IEnumerable<string> Keys { get; set; }

        // Logging
        [Option('v', "verbose",
            Required = false, Default = false,
            HelpText = "Enable Dokan logging from mounted filesystem")]
        public bool Logging { get; set; }
    }

    class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool SetDllDirectory(string lpPathName);

        static void Main(string[] args)
        {
            string sAppPath = new Uri(Path.GetDirectoryName(Assembly.GetExecutingAssembly().
                GetName().CodeBase)).LocalPath;
            string libFolder = (IntPtr.Size == 4) ? "x86" : "x64";
            SetDllDirectory(Path.Combine(sAppPath, libFolder));
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Start);
        }

        static void Start(Options options)
        {
            var auths = GetAuthMechanisms(options);

            var fs = auths
                .Select(auth => AttemptConnection(auth.Item1, auth.Item2, options.Path))
                .FirstOrDefault(result => result != null);

            if (fs == null)
                throw new InvalidOperationException(
                    "Could not connect to server with any known authentication mechanism");

            fs.Disconnected += (sender, e) => Environment.Exit(0);
            fs.ErrorOccurred += (sender, e) => Environment.Exit(0);

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) {
                e.Cancel = true;
                Console.WriteLine("Ctrl-C detected, start exiting . . .");
                Dokan.Unmount(options.DriveLetter);
            };

            fs.Mount($"{options.DriveLetter}", options.Logging ? null : new ConsoleLogger());
            Console.WriteLine("Program exited gracefully.");
        }

        static IEnumerable<(string, ConnectionInfo)> GetAuthMechanisms(Options options)
        {
            var auths = new List<(string, ConnectionInfo)>();

            if (options.Keys != null && options.Keys.Any())
            {
                auths.AddRange(new(string, ConnectionInfo)[]
                {
                    ("private key", PrivateKeyConnectionInfo(options))
                });
            }
            else if (!string.IsNullOrEmpty(options.Password))
            {
                Console.WriteLine("No SSH key file selected, using password auth instead.");

                auths.AddRange(new(string, ConnectionInfo)[]
                {
                    ("password", new PasswordConnectionInfo(options.Host, options.Port, options.Username, options.Password)),
                    ("keyboard-interactive", KeyboardInteractiveConnectionInfo(options, options.Password))
                });
            }
            else
            {
                Console.WriteLine(
                    "No key files specified, and password auth not enabled (win-sshfs does not search for private keys). Aborting...");
                Environment.Exit(1);
            }

            return auths;
        }

        static PrivateKeyConnectionInfo PrivateKeyConnectionInfo(Options options)
        {
            var pkFiles = options.Keys.Select(k =>
                !string.IsNullOrEmpty(options.Password)
                    ? new PrivateKeyFile(k, options.Password)
                    : new PrivateKeyFile(k));

            return new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, pkFiles.ToArray());
        }

        static KeyboardInteractiveConnectionInfo KeyboardInteractiveConnectionInfo(Options options, string pass)
        {
            var auth = new KeyboardInteractiveConnectionInfo(options.Host, options.Port, options.Username);

            auth.AuthenticationPrompt += (sender, e) =>
            {
                var passPrompts = e.Prompts
                    .Where(p => p.Request.StartsWith("Password:"));
                foreach (var p in passPrompts)
                    p.Response = pass;
            };

            return auth;
        }

        static SftpFilesystem AttemptConnection(string authType, ConnectionInfo connInfo, string path)
        {
            try
            {
                var sftpFS = new SftpFilesystem(connInfo, path);
                sftpFS.Connect();
                Console.WriteLine($"Successfully authenticated with {authType}.");
                return sftpFS;
            }
            catch (SshAuthenticationException)
            {
                Console.WriteLine(
                    $"Failed to authenticate using {authType}, falling back to next auth mechanism if available.");
                return null;
            }
        }
    }
}