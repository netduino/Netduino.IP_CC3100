using System;
using System.Reflection;

namespace Netduino.IP
{
    internal class Application
    {
        static System.Threading.Thread _applicationStartThread = null;

        static Application()
        {
            /* NOTE: this code will run automatically when the application begins */
            _applicationStartThread = new System.Threading.Thread(ApplicationStartThread);
            _applicationStartThread.Start();
        }

        static void ApplicationStartThread()
        {
            Type socketNativeType = Type.GetType("Netduino.IP.LinkLayers.CC3100SocketNative, Netduino.IP.LinkLayers.CC3100");
            System.Reflection.MethodInfo initializeMethod = socketNativeType.GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            initializeMethod.Invoke(null, new object[] { });
        }
    }
}
