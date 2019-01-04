﻿/* -LICENSE-START-
** Copyright (c) 2009 Blackmagic Design
**
** Permission is hereby granted, free of charge, to any person or organization
** obtaining a copy of the software and accompanying documentation covered by
** this license (the "Software") to use, reproduce, display, distribute,
** execute, and transmit the Software, and to prepare derivative works of the
** Software, and to permit third-parties to whom the Software is furnished to
** do so, all subject to the following:
** 
** The copyright notices in the Software and this entire statement, including
** the above license grant, this restriction and the following disclaimer,
** must be included in all copies of the Software, in whole or in part, and
** all derivative works of the Software, unless such copies or derivative
** works are solely in the form of machine-executable object code generated by
** a source language processor.
** 
** THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
** IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
** FITNESS FOR A PARTICULAR PURPOSE, TITLE AND NON-INFRINGEMENT. IN NO EVENT
** SHALL THE COPYRIGHT HOLDERS OR ANYONE DISTRIBUTING THE SOFTWARE BE LIABLE
** FOR ANY DAMAGES OR OTHER LIABILITY, WHETHER IN CONTRACT, TORT OR OTHERWISE,
** ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
** DEALINGS IN THE SOFTWARE.
** -LICENSE-END-
*/
using System;
using System.Windows.Forms;

using System.Runtime.InteropServices;
using DeckLinkAPI;
using System.Collections.Generic;
using System.Threading;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace open3mod
{
    public partial class OutputGenerator : Form
    {
        enum OutputSignal
        {
            kOutputSignalPip = 0,
            kOutputSignalDrop = 1
        };
        const uint m_prerollFrames = 4;//1 does not work, 2 goes for 12fps, 3 is first which works somehow
        const uint kAudioWaterlevel = 48000 / 25 * m_prerollFrames;
        private IReadOnlyList<int> kAudioChannels = new List<int> {2, 8, 16};

        private bool m_running;

        private DeckLinkDeviceDiscovery m_deckLinkDiscovery;
        private DeckLinkOutputDevice m_selectedDevice;
        //
        private int m_frameWidth;
        private int m_frameHeight;
        private long m_frameDuration;
        private long m_frameTimescale;
        private uint m_framesPerSecond;
        public IDeckLinkMutableVideoFrame[] m_videoFrame = new IDeckLinkMutableVideoFrame[m_prerollFrames];
        public int currentVideoFrame = 0;
        private IDeckLinkMutableVideoFrame m_videoFrameYUVBars;
        private IDeckLinkMutableVideoFrame m_videoFrameBGRAARGBBars;
        private IDeckLinkMutableVideoFrame m_videoFrameARGBBGRABars;
        private readonly MainWindow m_mainWindow;

        //        private IDeckLinkMutableVideoFrame m_videoFrameBGRA;
        private uint m_totalFramesScheduled;
        //
//        private OutputSignal m_outputSignal;
        private IntPtr m_audioBuffer;
        private uint m_audioBufferWriteOffset;
        private uint m_audioBufferReadOffset;
        private uint m_audioBufferSampleLength;
        private uint m_audioSamplesPerFrame;
        private uint m_audioDataPerSample;
        private uint m_audioChannelCount;
        private uint m_audioDataPerFrame;
        private _BMDAudioSampleRate m_audioSampleRate;
        private _BMDAudioSampleType m_audioSampleDepth;
        private _BMDPixelFormat m_pixelFormat;
        private int m_number = 1;
        private bool m_audioBufferAllocated = false;
        public bool isOutputFresh = false;

        public OutputGenerator(MainWindow mainWindow)
        {
            InitializeComponent();
            m_mainWindow = mainWindow;
            m_running = false;

            m_deckLinkDiscovery = new DeckLinkDeviceDiscovery();

            m_deckLinkDiscovery.DeviceArrived += new DeckLinkDiscoveryHandler((d) => this.Invoke((Action)(() => AddDevice(d))));
            m_deckLinkDiscovery.DeviceRemoved += new DeckLinkDiscoveryHandler((d) => this.Invoke((Action)(() => RemoveDevice(d))));

            m_pixelFormat = _BMDPixelFormat.bmdFormat10BitRGB; //jede, GL output zapsaný do frame dá: duhové barvy 10bit
            m_pixelFormat = _BMDPixelFormat.bmdFormat10BitYUV;//jede, pruhy svisle 10bit
            m_pixelFormat = _BMDPixelFormat.bmdFormat8BitYUV;//jede, pruhy...
            m_pixelFormat = _BMDPixelFormat.bmdFormat8BitBGRA;//správné pořadí, OK
               //    m_pixelFormat = _BMDPixelFormat.bmdFormat8BitARGB;//jiné možné pořadí, OK.

            this.ShowInTaskbar = false;
            m_audioSampleDepth = _BMDAudioSampleType.bmdAudioSampleType16bitInteger;
        }
        IDeckLinkVideoConversion frameConverter = new CDeckLinkVideoConversion();

        void AddDevice(IDeckLink decklinkDevice)
        {
            DeckLinkOutputDevice deckLink = new DeckLinkOutputDevice(decklinkDevice);

            if (deckLink.deckLinkOutput != null)
            {
                comboBoxOutputDevice.BeginUpdate();
                comboBoxOutputDevice.Items.Add(new StringObjectPair<DeckLinkOutputDevice>(deckLink.deviceName, deckLink));
                comboBoxOutputDevice.EndUpdate();

                if (comboBoxOutputDevice.Items.Count == m_number)
                {
                    comboBoxOutputDevice.SelectedIndex = m_number-1;
                  //  StartRunning();
                }
            }
        }

        void RemoveDevice(IDeckLink decklinkDevice)
        {
            // Stop capture if the selected device was removed
            if (m_selectedDevice != null && m_selectedDevice.deckLink == decklinkDevice && m_running)
            {
                // Stop running and disable output, we will not receive ScheduledPlaybackHasStopped callback
                StopRunning();
                DisableOutput();
            }

            // Remove the device from the dropdown
            comboBoxOutputDevice.BeginUpdate();
            foreach (StringObjectPair<DeckLinkOutputDevice> item in comboBoxOutputDevice.Items)
            {
                if (item.value.deckLink == decklinkDevice)
                {
                    comboBoxOutputDevice.Items.Remove(item);
                    break;
                }
            }
            comboBoxOutputDevice.EndUpdate();

            if (comboBoxOutputDevice.Items.Count == 0)
            {
                EnableInterface(false);
                m_selectedDevice = null;
            }
            else if (m_selectedDevice.deckLink == decklinkDevice)
            {
                comboBoxOutputDevice.SelectedIndex = 0;
                buttonStartStop.Enabled = true;
            }
        }


        private void SignalGenerator_Load(object sender, EventArgs e)
        {
            EnableInterface(false);
            m_deckLinkDiscovery.Enable();
        }

        private void buttonStartStop_Click(object sender, EventArgs e)
        {
            if (m_running)
                StopRunning();
            else
                StartRunning();
        }


        public void StartRunning()
        {
            if (m_running) return;
                //     m_selectedDevice.VideoFrameCompleted += new DeckLinkVideoOutputHandler((b) => this.BeginInvoke((Action)(() => { ScheduleNextFrame(b); })));
                m_selectedDevice.VideoFrameCompleted += new DeckLinkVideoOutputHandler((b) =>  ScheduleNextFrame(b));
            m_selectedDevice.AudioOutputRequested += new DeckLinkAudioOutputHandler(() => this.BeginInvoke((Action)(() => { WriteNextAudioSamples(); })));//used only for preroll and when in sync troubles
            m_selectedDevice.PlaybackStopped += new DeckLinkPlaybackStoppedHandler(() => this.BeginInvoke((Action)(() => { DisableOutput(); })));

            m_audioChannelCount = 16;
            m_audioSampleDepth = _BMDAudioSampleType.bmdAudioSampleType16bitInteger;
            m_audioSampleRate = _BMDAudioSampleRate.bmdAudioSampleRate48kHz;
            //
            //- Extract the IDeckLinkDisplayMode from the display mode popup menu
            IDeckLinkDisplayMode videoDisplayMode;
            videoDisplayMode = ((DisplayModeEntry)comboBoxVideoFormat.SelectedItem).displayMode;
            m_frameWidth = videoDisplayMode.GetWidth();
            m_frameHeight = videoDisplayMode.GetHeight();
            videoDisplayMode.GetFrameRate(out m_frameDuration, out m_frameTimescale);
            // Calculate the number of frames per second, rounded up to the nearest integer.  For example, for NTSC (29.97 FPS), framesPerSecond == 30.
            m_framesPerSecond = (uint)((m_frameTimescale + (m_frameDuration - 1)) / m_frameDuration);
            var mode = videoDisplayMode.GetDisplayMode();
            // Set the video output mode
            m_selectedDevice.deckLinkOutput.EnableVideoOutput(videoDisplayMode.GetDisplayMode(), _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
            // Set the audio output mode
            m_selectedDevice.deckLinkOutput.EnableAudioOutput(m_audioSampleRate, m_audioSampleDepth, m_audioChannelCount, _BMDAudioOutputStreamType.bmdAudioOutputStreamContinuous);

            // Generate prerollFrames of audio
            m_audioBufferSampleLength = (uint)(m_prerollFrames * audioSamplesPerFrame());
            int m_audioBufferDataLength = (int)(m_audioBufferSampleLength * audioDataPerSample());
            m_audioBuffer = Marshal.AllocCoTaskMem(m_audioBufferDataLength);
            m_audioBufferAllocated = true;

            lock (m_selectedDevice)
            {
                // Zero the buffer (interpreted as audio silence)
                for (int i = 0; i < m_audioBufferDataLength; i++)
                    Marshal.WriteInt32(m_audioBuffer, i, 0);
                FillSine(new IntPtr(m_audioBuffer.ToInt64()), m_audioBufferSampleLength, m_audioChannelCount, m_audioSampleDepth);
                m_audioBufferReadOffset = 0;
                m_audioBufferWriteOffset = 0;
            }

            m_videoFrameYUVBars = CreateOutputVideoFrame(FillYUVColourBars, _BMDPixelFormat.bmdFormat8BitYUV);
            m_videoFrameBGRAARGBBars = CreateOutputVideoFrame(FillBGRAARGBColourBars, _BMDPixelFormat.bmdFormat8BitBGRA);
            m_videoFrameARGBBGRABars = CreateOutputVideoFrame(FillARGBBGRAColourBars, _BMDPixelFormat.bmdFormat8BitBGRA);
            for (int i=0; i<m_prerollFrames;i++) m_videoFrame[i] = CreateOutputVideoFrame(FillYUVColourBars, _BMDPixelFormat.bmdFormat8BitYUV);//last format is filler's format, not video frame's format

            buttonStartStop.Text = "Stop";

            // Begin video preroll by scheduling a second of frames in hardware
            m_totalFramesScheduled = 0;
            for (uint i = 0; i < m_prerollFrames; i++)
                ScheduleNextFrame(true);

            // Begin audio preroll.  This will begin calling our audio callback, which will then start the DeckLink output stream - StartScheduledPlayback.
            m_selectedDevice.deckLinkOutput.BeginAudioPreroll();
            //StopRunning();
            m_running = true;
            comboBoxOutputDevice.Enabled = false;
            comboBoxVideoFormat.Enabled = false;

        }

        public IntPtr videoFrameBuffer()
        {
            m_videoFrame[currentVideoFrame].GetBytes(out IntPtr buffer);
            return buffer;
        }

        public uint audioDataPerSample()
        {
            m_audioDataPerSample = m_audioChannelCount * (uint)m_audioSampleDepth / 8;
            return m_audioDataPerSample;
        }
        public uint audioSamplesPerFrame()
        {
            m_audioSamplesPerFrame = (uint)(((uint)m_audioSampleRate * m_frameDuration) / m_frameTimescale);
            return m_audioSamplesPerFrame;
        }

        public uint audioDataPerFrame()
        {
            m_audioDataPerFrame = audioSamplesPerFrame() * audioDataPerSample();
            return m_audioDataPerFrame;
        }

        public void addAudioFrame(IntPtr audioData)
        {
            if (!m_audioBufferAllocated) return;
            if (m_running == false) return;
            m_selectedDevice.deckLinkOutput.ScheduleAudioSamples(audioData, audioSamplesPerFrame(), 0, 0, out uint samplesWritten);
            //as we copy audio to BMD output directly, we do not need to maintain audio buffer, we just create copy of last frame for repeating, if needed. Buffer is always longer...
            lock (m_selectedDevice)
            {
                MainWindow.CopyMemory(m_audioBuffer, audioData, audioDataPerFrame());
            }
        }

        public void repeatAudioFrame()
        {
            if (!m_audioBufferAllocated) return;
            if (m_running == false) return;
            m_selectedDevice.deckLinkOutput.ScheduleAudioSamples(m_audioBuffer, audioSamplesPerFrame(), 0, 0, out uint samplesWritten);
        }

        void WriteNextAudioSamples()
        {
            // Write one frame of audio to the DeckLink API
            if (!m_audioBufferAllocated) return;
            // Make sure that playback is still active
            if (m_running == false) return;

            // Try to maintain the number of audio samples buffered in the API at a specified waterlevel
            uint bufferedSamples;
            m_selectedDevice.deckLinkOutput.GetBufferedAudioSampleFrameCount(out bufferedSamples);
            if (bufferedSamples < kAudioWaterlevel)//this happenes only when addAudioFrame is behind
            {
                m_mainWindow.Renderer.syncTrack(true, "RepeatingAudioAtFrm:" + m_totalFramesScheduled.ToString(), 13);
                m_selectedDevice.deckLinkOutput.ScheduleAudioSamples(m_audioBuffer, audioSamplesPerFrame(), 0, 0, out uint samplesWritten);
            }
        }

        public bool IsRunning()
        {
            return m_running;
        }

        public IDeckLinkMutableVideoFrame CreateUploadVideoFrame(int width, int height, int stride)
        {
            IDeckLinkMutableVideoFrame newFrame = null;
            m_selectedDevice.deckLinkOutput.CreateVideoFrame(width, height, stride, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out newFrame);
            return newFrame;
        }

        private IDeckLinkMutableVideoFrame CreateOutputVideoFrame(Action<IDeckLinkVideoFrame> fillFrame, _BMDPixelFormat fillerPxFormat = _BMDPixelFormat.bmdFormat8BitYUV)
        {
            IDeckLinkMutableVideoFrame  referenceFrame = null;
            IDeckLinkMutableVideoFrame  scheduleFrame = null;
            m_selectedDevice.deckLinkOutput.CreateVideoFrame(m_frameWidth, m_frameHeight, m_frameWidth * bytesPerPixel, m_pixelFormat, _BMDFrameFlags.bmdFrameFlagDefault, out scheduleFrame);
            if (m_pixelFormat == fillerPxFormat)
            {
                // Fill 8-bit YUV directly without conversion
                fillFrame(scheduleFrame);
            }
            else
            {
                int bpp = 4;
                if (fillerPxFormat == _BMDPixelFormat.bmdFormat8BitYUV) bpp = 2;
                // Pixel formats are different, first generate 8-bit YUV bars frame and convert to required format
                m_selectedDevice.deckLinkOutput.CreateVideoFrame(m_frameWidth, m_frameHeight, m_frameWidth * bpp, fillerPxFormat, _BMDFrameFlags.bmdFrameFlagDefault, out referenceFrame);
                fillFrame(referenceFrame);
                try
                {
                    frameConverter.ConvertFrame(referenceFrame, scheduleFrame);
                }
                catch
                {
                    return referenceFrame;
                }
            }
            return scheduleFrame;
        }

        public void StopRunning()
        {
            if (!m_running) return;
            long unused;
            m_selectedDevice.deckLinkOutput.StopScheduledPlayback(0, out unused, 1000);
            m_running = false;
            comboBoxOutputDevice.Enabled = true;
            comboBoxVideoFormat.Enabled = true;

        }

        private void DisableOutput()
        {
            m_selectedDevice.deckLinkOutput.DisableAudioOutput();
            m_selectedDevice.deckLinkOutput.DisableVideoOutput();
            m_selectedDevice.RemoveAllListeners();

            // free audio buffer
            Marshal.FreeCoTaskMem(m_audioBuffer);
            m_audioBufferAllocated = false;
            buttonStartStop.Text = "Start";
        }

        public long GetTimeInFrame()
        {
            if (m_running == false) return 0;
            m_selectedDevice.deckLinkOutput.GetHardwareReferenceClock(1000, out long hardwareTime, out long timeInFrame, out long ticksPerFrame);
            return timeInFrame;
        }

        public void GetHardwareReferenceClock(out long hardwareTime, out long timeInFrame, out long ticksPerFrame)
        {
            hardwareTime = 0;
            timeInFrame = 0;
            ticksPerFrame = MainWindow.mainTiming;
            if (m_running == false) return;
            m_selectedDevice.deckLinkOutput.GetHardwareReferenceClock(1000, out hardwareTime, out timeInFrame, out ticksPerFrame);
        }

        bool isAlreadyScheduling;
        public void ScheduleNextFrame(bool prerolling)
        {
            m_selectedDevice.deckLinkOutput.GetBufferedVideoFrameCount(out uint buffered);
            string str = buffered.ToString().PadLeft(2);
            if (prerolling == false)
            {
                // If not prerolling, make sure that playback is still active
                if (m_running == false)  return;
                // Or if we are not waiting for something
                if (isAlreadyScheduling)
                {
                    m_mainWindow.Renderer.syncTrack(true, "EscapingScheduling, " + str + " bufd", 1);
                    return;
                }
            }
            isAlreadyScheduling = true;
            m_mainWindow.Renderer.syncTrack(false, "Scheduling", 1 );
            lock (m_videoFrame)
            {
                if (isOutputFresh == false) m_mainWindow.Renderer.syncTrack(true,"Reusing output frame", 2);
                isOutputFresh = false;
                if (buffered < m_prerollFrames)
                {
                    m_selectedDevice.deckLinkOutput.ScheduleVideoFrame(m_videoFrame[currentVideoFrame], (m_totalFramesScheduled * m_frameDuration), m_frameDuration, m_frameTimescale);
                    m_totalFramesScheduled += 1;
                }
                if ((buffered < 1) && (!prerolling))
                {
                    m_selectedDevice.deckLinkOutput.ScheduleVideoFrame(m_videoFrame[currentVideoFrame], (m_totalFramesScheduled * m_frameDuration), m_frameDuration, m_frameTimescale);
                    m_totalFramesScheduled += 1;
                    m_selectedDevice.deckLinkOutput.ScheduleVideoFrame(m_videoFrame[currentVideoFrame], (m_totalFramesScheduled * m_frameDuration), m_frameDuration, m_frameTimescale);
                    m_totalFramesScheduled += 1;
                    addAudioFrame(m_audioBuffer);//we need to add an audioframe more to keep sync
                    m_mainWindow.Renderer.syncTrack(true, "Buffered " +buffered.ToString()+" frames, Upbuffered 2 frames!! , total "+ m_totalFramesScheduled.ToString(), 12);

                }
            }
            isAlreadyScheduling = false;
            currentVideoFrame++;
            if (currentVideoFrame == m_prerollFrames) currentVideoFrame = 0;
            m_mainWindow.callRender();//starts rendering loop for a new frame
        }

        private void ScheduleNextFrameOrig(bool prerolling)
        {
            if (prerolling == false)
            {
                // If not prerolling, make sure that playback is still active
                if (m_running == false)
                    return;
            }

            if ((m_totalFramesScheduled % (m_framesPerSecond * 2)) < 15) //first 15 frames from two seconds 
            {
                // On each second, schedule a frame of bars
                m_selectedDevice.deckLinkOutput.ScheduleVideoFrame(m_videoFrameYUVBars, (m_totalFramesScheduled * m_frameDuration), m_frameDuration, m_frameTimescale);
            }
            else
            {
                // Schedule frames of other bars
                m_selectedDevice.deckLinkOutput.ScheduleVideoFrame(m_videoFrameBGRAARGBBars, (m_totalFramesScheduled * m_frameDuration), m_frameDuration, m_frameTimescale);
            }

            m_totalFramesScheduled += 1;
        }

        private void DisplayModeChanged(IDeckLinkDisplayMode newDisplayMode)
        {
            foreach (DisplayModeEntry item in comboBoxVideoFormat.Items)
            {
                if (item.displayMode.GetDisplayMode() == newDisplayMode.GetDisplayMode())
                    comboBoxVideoFormat.SelectedItem = item;
            }
        }

        private void comboBoxOutputDevice_SelectedValueChanged(object sender, EventArgs e)
        {
            m_selectedDevice = null;

            if (comboBoxOutputDevice.SelectedIndex < 0)
                return;

            m_selectedDevice = ((StringObjectPair<DeckLinkOutputDevice>)comboBoxOutputDevice.SelectedItem).value;

            // Update the video mode popup menu
            RefreshVideoModeList();

            // Enable the interface
            EnableInterface(true);
        }

        private void EnableInterface(bool enabled)
        {
            comboBoxOutputDevice.Enabled = enabled;
            comboBoxVideoFormat.Enabled = enabled;
            buttonStartStop.Enabled = enabled;
       }

        private void RefreshVideoModeList()
        {
            if (m_selectedDevice != null)
            {
                comboBoxVideoFormat.BeginUpdate();
                comboBoxVideoFormat.Items.Clear();

                int count = 0;
                foreach (IDeckLinkDisplayMode displayMode in m_selectedDevice)
                { comboBoxVideoFormat.Items.Add(new DisplayModeEntry(displayMode));
                    // if (displayMode = bmdModeHD1080i50) comboBoxVideoFormat.SelectedIndex=count;
                    count++;
                        }
                comboBoxVideoFormat.SelectedIndex = 7;

                comboBoxVideoFormat.EndUpdate();
            }
        }

        private int bytesPerPixel
        {
            get
            {
                int bytesPerPixel = 2;

                switch (m_pixelFormat)
                {
                    case _BMDPixelFormat.bmdFormat8BitYUV:
                        bytesPerPixel = 2;
                        break;
                    case _BMDPixelFormat.bmdFormat8BitARGB:
                    case _BMDPixelFormat.bmdFormat10BitYUV:
                    case _BMDPixelFormat.bmdFormat10BitRGB:
                    case _BMDPixelFormat.bmdFormat8BitBGRA:
                        bytesPerPixel = 4;
                        break;
                }
                return bytesPerPixel;
            }
        }


        #region buffer filling
        /*****************************************/

        void FillSine(IntPtr audioBuffer, uint samplesToWrite, uint channels, _BMDAudioSampleType sampleDepth)
        {
            if ((uint)sampleDepth == 16)
            {
                Int16[] buffer = new Int16[channels * samplesToWrite];

                for (uint i = 0; i < samplesToWrite; i++)
                {
 //                   Int16 sample = (Int16)(24576.0 * Math.Sin((i * 2.0 * Math.PI) / 48.0));
                    Int16 sample = (Int16)(76.0 * Math.Sin((i * 2.0 * Math.PI) / 48.0));
                    for (uint ch = 0; ch < channels; ch++)
                    {
                        buffer[i * channels + ch] = sample;
                    }
                }
                // Copy it into unmanaged buffer
                Marshal.Copy(buffer, 0, audioBuffer, (int)(channels * samplesToWrite));
            }
            else if ((uint)sampleDepth == 32)
            {
                Int32[] buffer = new Int32[channels * samplesToWrite];

                for (uint i = 0; i < samplesToWrite; i++)
                {
                    Int32 sample = (Int32)(1610612736.0 * Math.Sin((i * 2.0 * Math.PI) / 48.0));
                    for (uint ch = 0; ch < channels; ch++)
                    {
                        buffer[i * channels + ch] = sample;
                    }
                }
                // Copy it into unmanaged buffer
                Marshal.Copy(buffer, 0, audioBuffer, (int)(channels * samplesToWrite));

            }
        }

        void FillYUVColourBars(IDeckLinkVideoFrame theFrame)
        {
            IntPtr          buffer;
            int             width, height;
            UInt32[]        bars = {0xEA80EA80, 0xD292D210, 0xA910A9A5, 0x90229035, 0x6ADD6ACA, 0x51EF515A, 0x286D28EF, 0x10801080}; //Color Bars in YUV color format
            int             index = 0;

            theFrame.GetBytes(out buffer);
            width = theFrame.GetWidth();
            height = theFrame.GetHeight();

            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x += 2)
                {
                    // Write directly into unmanaged buffer
                    Marshal.WriteInt32(buffer, index * 4, (Int32)bars[(x * 8) / width]);
                    index++;
                }
            }
        }

        void FillBGRAARGBColourBars(IDeckLinkVideoFrame theFrame)
        {
            IntPtr buffer;
            int width, height;
    //      UInt32[] bars = { 0xFFEAEAEA, 0xFFEAEA10, 0xFF10EAEA, 0xFF10EA10, 0xFFEA10EA, 0xFFEA1010, 0xFF1010EA, 0xFF101010 }; //ColorBars in BGRA color format
     //       UInt32[] bars = { 0xFFFFFFFF, 0xFFFFFF00, 0xFF00FFFF, 0xFF00FF00, 0xFFFF00FF, 0xFFFF0000, 0xFF0000FF, 0xFF000000 }; //ColorBars in BGRA color format - watch REC601/709 when converting!
            UInt32[] bars = { 0x101010FF, 0x1010FF10, 0x10FF1010, 0xFF101010, 0xFF101010, 0x10FF1010, 0x1010FF10, 0x101010FF,  };//Bars BGRAARGB in BGRA color format

            int index = 0;

            theFrame.GetBytes(out buffer);
            width = theFrame.GetWidth();
            height = theFrame.GetHeight();

            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    // Write directly into unmanaged buffer
                    Marshal.WriteInt32(buffer, index * 4, (Int32)bars[(x * 8) / width]);
                    index++;
                }
            }
        }

        void FillARGBBGRAColourBars(IDeckLinkVideoFrame theFrame)
        {
            IntPtr buffer;
            int width, height;
            UInt32[] bars = { 0xFF101010, 0x10FF1010, 0x1010FF10, 0x101010FF, 0x101010FF, 0x1010FF10, 0x10FF1010, 0xFF101010 };//Bars ARGBBGRA in BGRA color format

            int index = 0;

            theFrame.GetBytes(out buffer);
            width = theFrame.GetWidth();
            height = theFrame.GetHeight();

            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    // Write directly into unmanaged buffer
                    Marshal.WriteInt32(buffer, index * 4, (Int32)bars[(x * 8) / width]);
                    index++;
                }
            }
        }

        void FillYUVBlack(IDeckLinkVideoFrame theFrame)
        {
            IntPtr buffer;
            int             width, height;
            int             wordsRemaining;
            UInt32          black = 0x10801080;
            int             index = 0;

            theFrame.GetBytes(out buffer);
            width = theFrame.GetWidth();
            height = theFrame.GetHeight();

            wordsRemaining = (width * 2 * height) / 4;

            while (wordsRemaining-- > 0)
            {
                Marshal.WriteInt32(buffer, index*4, (Int32)black);
                index++;
            }
        }

        #endregion

        /// <summary>
        /// Used for putting the IDeckLinkDisplayMode objects into the video format
        /// combo box.
        /// </summary>
        struct DisplayModeEntry
        {
            public IDeckLinkDisplayMode displayMode;

            public DisplayModeEntry(IDeckLinkDisplayMode displayMode)
            {
                this.displayMode = displayMode;
            }

            public override string ToString()
            {
                string str;
                displayMode.GetName(out str);
                return str;
            }
        }

        /// <summary>
        /// Used for putting other object types into combo boxes.
        /// </summary>
        struct StringObjectPair<T>
        {
            public string name;
            public T value;

            public StringObjectPair(string name, T value)
            {
                this.name = name;
                this.value = value;
            }

            public override string ToString()
            {
                return name;
            }
        }

        private void OutputGenerator_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            else StopRunning();
        }
    }
}
