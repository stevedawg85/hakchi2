using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace com.clusterrr.hakchi_gui.Ssh
{
    public class SshClientWrapper
    {
        private SshClient sshClient;
        private bool hasConnected;
        private string host;

        public bool IsConnected
        {
            get
            {
                return hasConnected ? sshClient.IsConnected : false;
            }
        }

        public string ClientIP
        {
            get
            {
                return host;
            }
        }

        public SshClientWrapper(string host, int port, string username, string password)
        {
            this.hasConnected = false;
            this.host = host;
            this.sshClient = new SshClient(host, port, username, password);
            sshClient.ErrorOccurred += SshClient_OnError;
        }

        public void Connect()
        {
            sshClient.Connect();
            hasConnected = true;

            if (!sshClient.IsConnected)
            {
                throw new SshClientException(string.Format("Unable to connect to SSH server at {0}:{1}", 
                    sshClient.ConnectionInfo.Host, sshClient.ConnectionInfo.Port));
            }
        }

        public void Disconnect()
        {
            if (sshClient.IsConnected)
            {
                sshClient.Disconnect();
            }
        }

        public string ExecuteSimple(string command, int timeout = 2000, bool throwOnNonZero = false)
        {
            SshCommand sshCommand = sshClient.CreateCommand(command);
            sshCommand.CommandTimeout = new TimeSpan(0, 0, 0, 0, timeout);
            string result = sshCommand.Execute();

            if (sshCommand.ExitStatus != 0 && throwOnNonZero)
            {
                throw new SshClientException(string.Format("Shell command \"{0}\" returned exit code {1} {2}", command, sshCommand.ExitStatus, sshCommand.Error));
            }

#if DEBUG
            Debug.WriteLine(string.Format("{0} # exit code {1}", command, sshCommand.ExitStatus));
#endif

            return result.Trim();
        }

        public int Execute(string command, Stream stdin = null, Stream stdout = null, Stream stderr = null, int timeout = 0, bool throwOnNonZero = false)
        {
            SshCommand sshCommand = sshClient.CreateCommand(command);
            sshCommand.CommandTimeout = new TimeSpan(0, 0, 0, 0, timeout);
            IAsyncResult execResult = sshCommand.BeginExecute(null, null, stdout, stderr);

            if (stdin != null)
            {
                try
                {
                    stdin.Seek(0, SeekOrigin.Begin);
                }
                catch
                {
                    // no-op
                }

                sshCommand.SendData(stdin);
            }

            sshCommand.EndExecute(execResult);

            if (sshCommand.ExitStatus != 0 && throwOnNonZero)
            {
                throw new SshClientException(string.Format("Shell command \"{0}\" returned exit code {1} {2}", command, sshCommand.ExitStatus, sshCommand.Error));
            }

#if DEBUG
            Debug.WriteLine(string.Format("{0} # exit code {1}", command, sshCommand.ExitStatus));
#endif

            return sshCommand.ExitStatus;
        }

        private void SshClient_OnError(object src, Renci.SshNet.Common.ExceptionEventArgs args)
        {
#if VERY_DEBUG
            Debug.WriteLine(string.Format("Error occurred on SSH client: {0}\n{1}\n{2}",
                args.Exception.Message, args.Exception.InnerException, args.Exception.StackTrace));
#endif

            if (sshClient.IsConnected)
            {
                sshClient.Disconnect();
            }
        }
    }
}
