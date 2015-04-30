using System;
using Microsoft.SPOT;

namespace Netduino.IP.LinkLayers
{
    public delegate void CC3100DataReceivedEventHandler(object sender, EventArgs e);
    interface ICC3100Transport : IDisposable 
    {
        /* NOTE: ideally, we would just use System.EventHandler for this event handler. */
        event CC3100DataReceivedEventHandler DataReceived;
        int BytesToRead { get; }
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
    }
}
