﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace AtmoLight.Targets
{
  public class HueHandler : ITargets
  {
    #region Fields

    public Target Name { get { return Target.Hue; } }
    public TargetType Type { get { return TargetType.Network; } }
    public bool AllowDelay { get { return false; } }
    public List<ContentEffect> SupportedEffects
    {
      get
      {
        return new List<ContentEffect> {  ContentEffect.GIFReader,
                                          ContentEffect.LEDsDisabled,
                                          ContentEffect.MediaPortalLiveMode,
                                          ContentEffect.StaticColor,
                                          ContentEffect.VUMeter,
                                          ContentEffect.VUMeterRainbow
        };
      }
    }

    // CORE
    private Core coreObject;

    // HUE
    private int hueDelayAtmoHue = 5000; 
    private int hueReconnectCounter = 0;
    private Boolean HueBridgeStartOnResume = false;

    // TCP
    private static TcpClient Socket = new TcpClient();
    private Stream Stream;

    // Color checks
    private int avgR_previousLive = 0;
    private int avgG_previousLive = 0;
    private int avgB_previousLive = 0;
    private int avgR_previousVU = 0;
    private int avgG_previousVU = 0;
    private int avgB_previousVU = 0;

    // Locks
    private bool isInit = false;
    private volatile bool initLock = false;
    private bool isAtmoHueRunning = false;

    private enum APIcommandType
    {
      Color,
      Group,
      Power,
      Room,
    }

    #endregion

    #region Hue
    public HueHandler()
    {
      coreObject = Core.GetInstance();
    }

    public void Initialise(bool force = false)
    {
      if (!initLock)
      {
        //Set Init lock
        initLock = true;
        isInit = true;
        hueReconnectCounter = 0;
        Thread t = new Thread(() => InitialiseThread(force));
        t.IsBackground = true;
        t.Start();
      }
      else
      {
        Log.Debug("HueHandler - Initialising locked.");
      }

    }

    private bool InitialiseThread(bool force = false)
    {
      if (!Win32API.IsProcessRunning("atmohue.exe") && coreObject.hueIsRemoteMachine == false)
      {
        if (coreObject.hueStart)
        {
          isAtmoHueRunning = StartHue();
          System.Threading.Thread.Sleep(hueDelayAtmoHue);
          if (isAtmoHueRunning)
          {
            Connect();
          }
        }
        else
        {
          Log.Error("HueHandler - AtmoHue is not running.");
          initLock = false;
          return false;
        }
      }
      else
      {
        isAtmoHueRunning = true;
        if (Socket.Connected)
        {
          Log.Debug("HueHandler - already connect to AtmoHue");
          initLock = false;
          return true;
        }
        else
        {
          Connect();
          return true;
        }
      }
      return true;
    }


    public void ReInitialise(bool force = false)
    {
      if (coreObject.reInitOnError || force)
      {
        Thread t = new Thread(() => Initialise(force));
        t.IsBackground = true;
        t.Start();
      }
    }

    public void Dispose()
    {
      if (Socket.Connected)
      {
        Disconnect();
      }
    }

    public bool StartHue()
    {
      Log.Debug("HueHandler - Trying to start AtmoHue.");
      if (!System.IO.File.Exists(coreObject.huePath))
      {
        Log.Error("HueHandler - AtmoHue.exe not found!");
        initLock = false;
        return false;
      }
      
      Process Hue = new Process();
      Hue.StartInfo.FileName = coreObject.huePath;
      Hue.StartInfo.WorkingDirectory = Path.GetDirectoryName(coreObject.huePath);
      Hue.StartInfo.UseShellExecute = true;
      try
      {
        Hue.Start();
      }
      catch (Exception)
      {
        Log.Error("HueHander - Starting Hue failed.");
        initLock = false;
        return false;
      }
      Log.Info("HueHander - AtmoHue successfully started.");
      return true;
    }


    public bool IsConnected()
    {
      if (initLock)
      {
        return false;
      }

      return Socket.Connected;
    }

    private void Connect()
    {
      Thread t = new Thread(ConnectThread);
      t.IsBackground = true;
      t.Start();
    }
    private void ConnectThread()
    {

      while (hueReconnectCounter <= coreObject.hueReconnectAttempts)
      {
        if (!Socket.Connected)
        {
          //Close old socket and create new TCP client which allows it to reconnect when calling Connect()
          Disconnect();

          try
          {
            Socket = new TcpClient();

            Socket.SendTimeout = 5000;
            Socket.ReceiveTimeout = 5000;
            Socket.Connect(coreObject.hueIP, coreObject.huePort);
            Stream = Socket.GetStream();
            Log.Debug("HueHandler - Connected to AtmoHue");
          }
          catch (Exception e)
          {
            Log.Error("HueHandler - Error while connecting");
            Log.Error("HueHandler - Exception: {0}", e.Message);
          }

          //Increment times tried
          hueReconnectCounter++;

          //Show error if reconnect attempts exhausted
          if (hueReconnectCounter > coreObject.hueReconnectAttempts && !Socket.Connected)
          {
            Log.Error("HueHandler - Error while connecting and connection attempts exhausted");
            coreObject.NewConnectionLost(Name);
            break;
          }

          //Sleep for specified time
          Thread.Sleep(coreObject.hyperionReconnectDelay);
        }
        else
        {
          //Log.Debug("HueHandler - Connected after {0} attempts.", hyperionReconnectCounter);
          break;
        }
      }

      //Reset Init lock
      initLock = false;

      //Reset counter when we have finished
      hueReconnectCounter = 0;

      //Power ON bridge if connected and enabled
      if (HueBridgeStartOnResume)
      {
        //Reset start variable
        HueBridgeStartOnResume = false;

        if (Socket.Connected)
        {
          //Send Power ON command
          HueBridgePower("ON");

          //Sleep for 2s to allow for Hue Bridge startup
          Thread.Sleep(2000);
        }
      }
      //On first initialize set the effect after we are done trying to connect
      if (isInit && Socket.Connected)
      {
        ChangeEffect(coreObject.GetCurrentEffect());
        isInit = false;
      }
      else if (isInit)
      {
        isInit = false;
      }
    }
    private void Disconnect()
    {
      try
      {
        Socket.Close();
      }
      catch (Exception e)
      {
        Log.Error(string.Format("HueHandler - {0}", "Error during disconnect"));
        Log.Error(string.Format("HueHandler - {0}", e.Message));
      }
    }

    private void sendAPIcommand(string message)
    {
      try
      {
        ASCIIEncoding encoder = new ASCIIEncoding();
        byte[] buffer = encoder.GetBytes(message);

        Stream.Write(buffer, 0, buffer.Length);
        Stream.Flush();
      }
      catch (Exception e)
      {
        Log.Error("HueHandler - error during sending power command");
        Log.Error(string.Format("HueHandler - {0}", e.Message));
        ReInitialise(false);
      }
    }

    public void ChangeColor(int red, int green, int blue, int priority, int brightness)
    {
      Thread t = new Thread(() => ChangeColorThread(red,green,blue,priority,brightness));
      t.IsBackground = true;
      t.Start();

    }
    public void ChangeColorThread(int red, int green, int blue, int priority, int brightness)
    {
      try
      {
        string message = string.Format("{0},{1},{2},{3},{4},{5},{6}", "ATMOLIGHT", APIcommandType.Color, red.ToString(), green.ToString(), blue.ToString(), priority.ToString(), brightness.ToString());
        sendAPIcommand(message);
      }
      catch (Exception e)
      {
        Log.Error("HueHandler - error during sending color");
        Log.Error(string.Format("HueHandler - {0}", e.Message));
        ReInitialise(false);
      }
    }
    public bool ChangeEffect(ContentEffect effect)
    {
      if (!IsConnected())
      {
        return false;
      }
      switch (effect)
      {
        case ContentEffect.StaticColor:
          ChangeColor(coreObject.staticColor[0], coreObject.staticColor[1], coreObject.staticColor[2], 10, 0);
          break;
        case ContentEffect.LEDsDisabled:
        case ContentEffect.Undefined:
        default:
          ChangeColor(0, 0, 0, 1, 0);
          break;
      }
      return true;
    }
    public void ChangeProfile()
    {
      return;
    }
    public void ChangeImage(byte[] pixeldata, byte[] bmiInfoHeader)
    {
      if (!IsConnected())
      {
        return;
      }

      //Convert pixeldata to bitmap and calculate average color afterwards
      try
      {
        unsafe
        {
          fixed (byte* ptr = pixeldata)
          {

            using (Bitmap image = new Bitmap(coreObject.GetCaptureWidth(), coreObject.GetCaptureHeight(), coreObject.GetCaptureWidth() * 4,
                        PixelFormat.Format32bppRgb, new IntPtr(ptr)))
            {
              if (coreObject.GetCurrentEffect() == ContentEffect.VUMeter || coreObject.GetCurrentEffect() == ContentEffect.VUMeterRainbow)
              {
                CalculateVUMeterColorAndSendToHue(image);
              }
              else
              {
                CalculateAverageColorAndSendToHue(image);
              }
            }
          }
        }
      }
      catch(Exception e)
      {
        Log.Error(string.Format("HueHandler - {0}", "Error during average color calculations"));
        Log.Error(string.Format("HueHandler - {0}", e.Message));
      }
    }
    public void setActiveGroup(string groupName)
    {
      Log.Debug(APIcommandType.Group + " --> " + groupName);
      string message = string.Format("{0},{1},{2},{3}", "ATMOLIGHT", APIcommandType.Group, "OnlyActivate", groupName);
      sendAPIcommand(message);
    }
    public void setGroupStaticColor(string groupName, string colorName)
    {
      Log.Debug(APIcommandType.Group + " --> " + groupName);
      string message = string.Format("{0},{1},{2},{3},{4}", "ATMOLIGHT", APIcommandType.Group, "SetStaticColor", groupName, colorName);
      sendAPIcommand(message);
    }
    public List<string> Loadgroups()
    {
      List<string> groups = new List<string>();

      try
      {
        string settingsLocation = Path.GetDirectoryName(coreObject.huePath) + "\\settings.xml";
        if (File.Exists(settingsLocation))
        {
          using (XmlReader reader = XmlReader.Create(settingsLocation))
          {
            while (reader.Read())
            {
              // LED Locations

              if ((reader.NodeType == XmlNodeType.Element) && (reader.Name == "LedLocation"))
              {
                reader.ReadToDescendant("Location");
                groups.Add(reader.ReadString());
              }

            }
          }
        }
      }
      catch(Exception e)
      {
        Log.Error(string.Format("HueHandler - {0}", "Error during reading group config"));
        Log.Error(string.Format("HueHandler - {0}", e.Message));
      }
      return groups;
    }
    public List<string> LoadStaticColors()
    {
      List<string> staticColors = new List<string>();

      try
      {
        string settingsLocation = Path.GetDirectoryName(coreObject.huePath) + "\\settings.xml";
        if (File.Exists(settingsLocation))
        {
          using (XmlReader reader = XmlReader.Create(settingsLocation))
          {
            while (reader.Read())
            {
              // LED Locations

              if ((reader.NodeType == XmlNodeType.Element) && (reader.Name == "LedStaticColor"))
              {
                reader.ReadToDescendant("Name");
                staticColors.Add(reader.ReadString());
              }
            }
          }
        }
      }
      catch (Exception e)
      {
        Log.Error(string.Format("HueHandler - {0}", "Error during reading static color config"));
        Log.Error(string.Format("HueHandler - {0}", e.Message));
      }
      return staticColors;
    }

    public void CalculateAverageColorAndSendToHue(Bitmap bm)
    {
      int width = bm.Width;
      int height = bm.Height;
      int red = 0;
      int green = 0;
      int blue = 0;
      int minDiversion = 15; // drop pixels that do not differ by at least minDiversion between color values (white, gray or black)
      int dropped = 0; // keep track of dropped pixels
      long[] totals = new long[] { 0, 0, 0 };
      int bppModifier = bm.PixelFormat == System.Drawing.Imaging.PixelFormat.Format24bppRgb ? 3 : 4; // cutting corners, will fail on anything else but 32 and 24 bit images

      BitmapData srcData = bm.LockBits(new System.Drawing.Rectangle(0, 0, bm.Width, bm.Height), ImageLockMode.ReadOnly, bm.PixelFormat);
      int stride = srcData.Stride;
      IntPtr Scan0 = srcData.Scan0;

      unsafe
      {
        byte* p = (byte*)(void*)Scan0;

        for (int y = 0; y < height; y++)
        {
          for (int x = 0; x < width; x++)
          {
            int idx = (y * stride) + x * bppModifier;
            red = p[idx + 2];
            green = p[idx + 1];
            blue = p[idx];
            if (Math.Abs(red - green) > minDiversion || Math.Abs(red - blue) > minDiversion || Math.Abs(green - blue) > minDiversion)
            {
              totals[2] += red;
              totals[1] += green;
              totals[0] += blue;
            }
            else
            {
              dropped++;
            }
          }
        }
      }

      int count = width * height - dropped;

      int minDifferencePreviousColors = coreObject.hueMinimalColorDifference;


      int avgR = 0;
      int avgG = 0;
      int avgB = 0;
      bool invalidColorValue = false;

      // Doesn't work all the time, will return divide by zero errors sometimes due to invalid values.
      // If we get an invalid value we return 0 and skip that image
      try
      {
        avgR = (int)(totals[2] / count);
      }
      catch
      {
        invalidColorValue = true;
      }

      try
      {
        avgG = (int)(totals[1] / count);
      }
      catch
      {
        invalidColorValue = true;
      }

      try
      {
        avgB = (int)(totals[0] / count);
      }
      catch
      {
        invalidColorValue = true;
      }

      //If users sets minimal difference to 0 disable the average color check
      if (minDifferencePreviousColors == 0 && invalidColorValue == false)
      {
        //Send average colors to Bridge
        ChangeColor(avgR, avgG, avgB, 200, 0);
      }
      else
      {
        //Minimal differcence new compared to previous colors
        if (Math.Abs(avgR_previousLive - avgR) > minDifferencePreviousColors || Math.Abs(avgG_previousLive - avgG) > minDifferencePreviousColors || Math.Abs(avgB_previousLive - avgB) > minDifferencePreviousColors)
        {
          avgR_previousLive = avgR;
          avgG_previousLive = avgG;
          avgB_previousLive = avgB;

          //Send average colors to Bridge
          if (invalidColorValue == false)
          {
            ChangeColor(avgR, avgG, avgB, 200, 0);
          }
        }
      }
    }

    private void CalculateVUMeterColorAndSendToHue(Bitmap vuMeterBitmap)
    {
      int minDifferencePreviousColors = coreObject.hueMinimalColorDifference;

      for (int i = 0; i < vuMeterBitmap.Height; i++)
      {
        if (vuMeterBitmap.GetPixel(0, i).R != 0 || vuMeterBitmap.GetPixel(0, i).G != 0 || vuMeterBitmap.GetPixel(0, i).B != 0)
        {
          int red = vuMeterBitmap.GetPixel(0, i).R;
          int green = vuMeterBitmap.GetPixel(0, i).G;
          int blue = vuMeterBitmap.GetPixel(0, i).B;

          if (Math.Abs(avgR_previousVU - red) > minDifferencePreviousColors || Math.Abs(avgG_previousVU - green) > minDifferencePreviousColors || Math.Abs(avgB_previousVU - blue) > minDifferencePreviousColors)
          {
            avgR_previousVU = red;
            avgG_previousVU = green;
            avgB_previousVU = blue;
            ChangeColor(red, green, blue, 200, 0);
          }
          return;
        }
        else if (vuMeterBitmap.GetPixel(vuMeterBitmap.Width - 1, i).R != 0 || vuMeterBitmap.GetPixel(vuMeterBitmap.Width - 1, i).G != 0 || vuMeterBitmap.GetPixel(vuMeterBitmap.Width - 1, i).B != 0)
        {
          int red = vuMeterBitmap.GetPixel(vuMeterBitmap.Width - 1, i).R;
          int green = vuMeterBitmap.GetPixel(vuMeterBitmap.Width - 1, i).G;
          int blue = vuMeterBitmap.GetPixel(vuMeterBitmap.Width - 1, i).B;

          if (Math.Abs(avgR_previousVU - red) > minDifferencePreviousColors || Math.Abs(avgG_previousVU - green) > minDifferencePreviousColors || Math.Abs(avgB_previousVU - blue) > minDifferencePreviousColors)
          {
            avgR_previousVU = red;
            avgG_previousVU = green;
            avgB_previousVU = blue;
            ChangeColor(red, green, blue, 200, 0);
          }
          return;
        }
      }
      ChangeColor(0, 0, 0,200, 0);
    }

    private void HueBridgePower(string powerCommand)
    {
      string message = string.Format("{0},{1},{2}", "ATMOLIGHT", APIcommandType.Power, powerCommand);
      sendAPIcommand(message);
    }

    #endregion

    #region powerstate monitoring
    public void PowerModeChanged(PowerModes powerMode)
    {
      switch (powerMode)
      {
        case PowerModes.Resume:

          // Close old socket
          Disconnect();

          //Reconnect to AtmoHue after standby
          Log.Debug("HueHandler - Initialising after standby");

          if (coreObject.hueBridgeEnableOnResume)
          {
            HueBridgeStartOnResume = true;
            Initialise();
          }
          else
          {
            Initialise();
          }
          break;
        case PowerModes.Suspend:
          if (coreObject.hueBridgeDisableOnSuspend)
          {
            //Send Power OFF command
            if (Socket.Connected)
            {
              HueBridgePower("OFF");
            }
          }
          break;
      }
    }
    #endregion

  }
  #region class Win32API
  public sealed class Win32API
  {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
      public int left;
      public int top;
      public int right;
      public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
      public uint dwSize;
      public uint cntUsage;
      public uint th32ProcessID;
      public IntPtr th32DefaultHeapID;
      public uint th32ModuleID;
      public uint cntThreads;
      public uint th32ParentProcessID;
      public int pcPriClassBase;
      public uint dwFlags;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
      public string szExeFile;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, String lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    private const int WM_CLOSE = 0x10;
    private const int WM_DESTROY = 0x2;

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, int wParam, int lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    public static extern Int64 GetTickCount();

    [DllImport("kernel32.dll")]
    private static extern int Process32First(IntPtr hSnapshot,
                                     ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern int Process32Next(IntPtr hSnapshot,
                                    ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags,
                                                   uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hSnapshot);
    private const int WM_MouseMove = 0x0200;

    public static void RefreshTrayArea()
    {

      RECT rect;

      IntPtr systemTrayContainerHandle = FindWindow("Shell_TrayWnd", null);
      IntPtr systemTrayHandle = FindWindowEx(systemTrayContainerHandle, IntPtr.Zero, "TrayNotifyWnd", null);
      IntPtr sysPagerHandle = FindWindowEx(systemTrayHandle, IntPtr.Zero, "SysPager", null);
      IntPtr notificationAreaHandle = FindWindowEx(sysPagerHandle, IntPtr.Zero, "ToolbarWindow32", null);
      GetClientRect(notificationAreaHandle, out rect);
      for (var x = 0; x < rect.right; x += 5)
        for (var y = 0; y < rect.bottom; y += 5)
          SendMessage(notificationAreaHandle, WM_MouseMove, 0, (y << 16) + x);
    }

    public static bool IsProcessRunning(string applicationName)
    {
      IntPtr handle = IntPtr.Zero;
      try
      {
        // Create snapshot of the processes
        handle = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        PROCESSENTRY32 info = new PROCESSENTRY32();
        info.dwSize = (uint)System.Runtime.InteropServices.
                      Marshal.SizeOf(typeof(PROCESSENTRY32));

        // Get the first process
        int first = Process32First(handle, ref info);

        // While there's another process, retrieve it
        do
        {
          if (string.Compare(info.szExeFile,
                applicationName, true) == 0)
          {
            return true;
          }
        }
        while (Process32Next(handle, ref info) != 0);
      }
      catch
      {
        throw;
      }
      finally
      {
        // Release handle of the snapshot
        CloseHandle(handle);
        handle = IntPtr.Zero;
      }
      return false;
    }
  }
  #endregion
}