using System;
using Microsoft.SPOT;

namespace Netduino.IP.LinkLayers
{
    class CC3100MaximumConcurrentActionsExceededException : Exception
    {
    }

    public class CC3100SimpleLinkException : Exception
    {
        Int32 _error;

        public CC3100SimpleLinkException(Int32 error) : base("SimpleLink ErrorCode " + error.ToString())
        {
            _error = error;
        }

        public Int32 Error
        {
            get
            {
                return _error;
            }
        }
    }
}
