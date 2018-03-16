using System;

namespace com.clusterrr.hakchi_gui.Ssh
{
    public class SshClientException : Exception
    {
        public SshClientException(string message) : base(message) { }
    }
}
