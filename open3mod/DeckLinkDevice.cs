﻿/* -LICENSE-START-
** Copyright (c) 2017 Blackmagic Design
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
using DeckLinkAPI;
using System.Collections.Generic;
using System.Threading;

namespace open3mod
{
    public delegate void DeckLinkInputSignalHandler(bool inputSignal);
    public delegate void DeckLinkFormatChangedHandler(IDeckLinkDisplayMode newDisplayMode);
    class DeckLinkInputDevice : IDeckLinkInputCallback, IEnumerable<IDeckLinkDisplayMode>
    {
        private IDeckLink           m_deckLink;
        private IDeckLinkInput      m_deckLinkInput;
        private bool                m_applyDetectedInputMode = true;
        private bool                m_currentlyCapturing = false;
        private bool                m_validInputSignal = false;
        const int                   m_memSize = 20;
        private int                 m_lastUsedVideoMem = -1;
        private int                 m_lastUsedAudioMem = -1;
        private int                 m_additionalDelay = 0;
        private IDeckLinkVideoInputFrame[]  m_videoMem = new IDeckLinkVideoInputFrame[m_memSize];
        private IDeckLinkAudioInputPacket[] m_audioMem = new IDeckLinkAudioInputPacket[m_memSize];
        private long[] m_timeInFrame = new long [m_memSize];
        private long m_frameNo = -1;
        private long m_lastFrameNo = -1;
        private long m_lastFrameDelay = 0;

        string m_pxFormat = "Unknown";
        private int m_number = 0;
        private MainWindow m_mainWindow;
        private CapturePreview  m_capturePreview;

        public void SetID(CapturePreview capturePreview, MainWindow mainWindow, int numb)
        {
            m_number = numb;
            m_capturePreview = capturePreview;
            m_mainWindow = mainWindow;
        }

        public DeckLinkInputDevice(IDeckLink deckLink)
        {
            m_deckLink = deckLink;

            // Get input interface
            try
            {
                m_deckLinkInput = (IDeckLinkInput)m_deckLink;
            }
            catch (InvalidCastException)
            {
                // No input interface found, eg in case of DeckLink Mini Monitor
                return;
            }
        }

        public event DeckLinkInputSignalHandler InputSignalChanged;
        public event DeckLinkFormatChangedHandler InputFormatChanged;

        public IDeckLink deckLink
        {
            get { return m_deckLink; }
        }

        public IDeckLinkInput deckLinkInput
        {
            get { return m_deckLinkInput; }
        }
        
        public string deviceName
        {
            get
            {
                string deviceName;
                m_deckLink.GetDisplayName(out deviceName);
                return deviceName;
            }
        }

        public bool isVideoLocked
        {
            get
            {
                int flag;
                var deckLinkStatus = (IDeckLinkStatus)m_deckLink;
                deckLinkStatus.GetFlag(_BMDDeckLinkStatusID.bmdDeckLinkStatusVideoInputSignalLocked, out flag);
                return flag != 0;
            }
        }

        public bool supportsFormatDetection
        {
            get
            {
                int flag;
                var deckLinkAttributes = (IDeckLinkProfileAttributes)m_deckLink;
                deckLinkAttributes.GetFlag(_BMDDeckLinkAttributeID.BMDDeckLinkSupportsInputFormatDetection, out flag);
                return flag != 0;
            }
        }

        public bool isCapturing
        {
            get { return m_currentlyCapturing; }
        }

        public string pxFormat
        {
            get
            {
                return m_pxFormat;
            }
        }

        public int additionalDelay
        {
            get
            {
                return m_additionalDelay;
            }
            set
            {
                m_additionalDelay = value;
                if (m_additionalDelay > m_memSize - 2) m_additionalDelay = m_memSize - 2;
                if (m_additionalDelay < 0) m_additionalDelay = 0;
            }
        }

        public void getNextFrame(out IDeckLinkVideoInputFrame videoFrame, out IDeckLinkAudioInputPacket audioPacket, out long frameDelay, out long difference )
        {
            difference = m_frameNo - m_lastFrameNo;
            frameDelay = 0;
            long newTimeInFrame = 0;
            if (!m_currentlyCapturing)
            {
                videoFrame = null;
                audioPacket = null;
                difference = -1;
                return;
            }
            switch (difference)
            {
                default:// we must skip/reuse frame as we have no new one, or simply any other frame;
                    videoFrame = m_videoMem[0 + m_additionalDelay];
                    audioPacket = m_audioMem[0 + m_additionalDelay];
                    newTimeInFrame = m_timeInFrame[0 + m_additionalDelay];
                    m_lastFrameNo = m_frameNo;
                    frameDelay = MainWindow.mainTiming - newTimeInFrame;
                    break;
                case 1:
                    videoFrame = m_videoMem[0 + m_additionalDelay];
                    audioPacket = m_audioMem[0 + m_additionalDelay];
                    newTimeInFrame = m_timeInFrame[0 + m_additionalDelay];
                    m_lastFrameNo = m_frameNo;
                    frameDelay = MainWindow.mainTiming - newTimeInFrame;
                    break;
                case 2:
                    videoFrame = m_videoMem[1 + m_additionalDelay];
                    audioPacket = m_audioMem[1 + m_additionalDelay];
                    newTimeInFrame = m_timeInFrame[1 + m_additionalDelay];
                    m_lastFrameNo = m_frameNo - 1;
                    frameDelay = MainWindow.mainTiming + MainWindow.mainTiming - newTimeInFrame;
                    break;
                    //both video and audio may happen to be null
            }

            if (newTimeInFrame < 5) //values between appx 0-4 delay are uncertain/unstable, depend on
            {
                frameDelay = m_lastFrameDelay;
            }
            m_lastFrameDelay = frameDelay;
        }

        public void skipNextFrame()
        {
            if (m_currentlyCapturing) m_lastFrameNo++;
        }

        void IDeckLinkInputCallback.VideoInputFormatChanged(_BMDVideoInputFormatChangedEvents notificationEvents, IDeckLinkDisplayMode newDisplayMode, _BMDDetectedVideoInputFormatFlags detectedSignalFlags)
        {
            // Restart capture with the new video mode if told to
            if (! m_applyDetectedInputMode)
                return;

            var pixelFormat = _BMDPixelFormat.bmdFormat10BitYUV;
            if (detectedSignalFlags.HasFlag(_BMDDetectedVideoInputFormatFlags.bmdDetectedVideoInputRGB444))
            {
                m_pxFormat = "10 bit RGB";
                pixelFormat = _BMDPixelFormat.bmdFormat10BitRGB;
            }
            if (detectedSignalFlags.HasFlag(_BMDDetectedVideoInputFormatFlags.bmdDetectedVideoInputYCbCr422))
            {
                m_pxFormat = "10 bit YUV";
                pixelFormat = _BMDPixelFormat.bmdFormat10BitYUV;
            }
            if (detectedSignalFlags.HasFlag(_BMDDetectedVideoInputFormatFlags.bmdDetectedVideoInputDualStream3D))
            {
                m_pxFormat = "Dual Stream 3D";
                pixelFormat = _BMDPixelFormat.bmdFormat10BitYUV;
            }

            // Stop the capture
            m_deckLinkInput.StopStreams();

            // Set the video input mode
            //For upload to GPU we need 8bitYUV
            // m_deckLinkInput.EnableVideoInput(newDisplayMode.GetDisplayMode(), pixelFormat, _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection);
            m_deckLinkInput.EnableVideoInput(newDisplayMode.GetDisplayMode(), _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection);
            m_deckLinkInput.EnableAudioInput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 16);

            // Start the capture
            m_deckLinkInput.StartStreams();

            InputFormatChanged(newDisplayMode);
        }

        void IDeckLinkInputCallback.VideoInputFrameArrived(IDeckLinkVideoInputFrame videoFrame, IDeckLinkAudioInputPacket audioPacket)
        {
          //  Thread.CurrentThread.Name = "FrameArrived";
            IntPtr audioData = (IntPtr)0;
            IntPtr videoData = (IntPtr)0;
            m_mainWindow.GetHardwareReferenceClock(out long hardwareTime, out long timeInFrame, out long ticksPerFrame);
            if (videoFrame != null)
            {
                bool inputSignalChange = videoFrame.GetFlags().HasFlag(_BMDFrameFlags.bmdFrameHasNoInputSource);
                if (inputSignalChange != m_validInputSignal)
                {
                    m_validInputSignal = inputSignalChange;
                    InputSignalChanged(m_validInputSignal);
                }
                if (inputSignalChange == false) //i.e. frame is valid
                {
                    lock (m_capturePreview.memLock)
                    {
                        if (m_videoMem[m_memSize - 1] != null)
                        {
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(m_videoMem[m_memSize - 1]);
                            m_videoMem[m_memSize - 1] = null;
                        }
                        for (int i = m_memSize - 1; i > 0; i--)
                        {
                            m_videoMem[i] = m_videoMem[i - 1];
                            m_timeInFrame[i] = m_timeInFrame[i - 1];
                        }
                        m_videoMem[0] = videoFrame;
                        m_timeInFrame[0] = timeInFrame;
                        if (m_lastUsedVideoMem < m_memSize - 2) m_lastUsedVideoMem++;
                        m_frameNo++;
                    }
                }
            }
            if (audioPacket != null)
            {
                lock (m_capturePreview.memLock)
                {
                    if (m_audioMem[m_memSize - 1] != null)
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(m_audioMem[m_memSize - 1]);
                        m_audioMem[m_memSize - 1] = null;
                    }
                    for (int i = m_memSize - 1; i > 0; i--)
                    {
                        m_audioMem[i] = m_audioMem[i - 1];
                    }
                    m_audioMem[0] = audioPacket;
                    if (m_lastUsedAudioMem < m_memSize - 2) m_lastUsedAudioMem++;
                }
            }
        }

        IEnumerator<IDeckLinkDisplayMode> IEnumerable<IDeckLinkDisplayMode>.GetEnumerator()
        {
            IDeckLinkDisplayModeIterator displayModeIterator;
            m_deckLinkInput.GetDisplayModeIterator(out displayModeIterator);
            return new DisplayModeEnum(displayModeIterator);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            throw new InvalidOperationException();
        }

        public void StartCapture(IDeckLinkDisplayMode displayMode, IDeckLinkScreenPreviewCallback screenPreviewCallback, bool applyDetectedInputMode)
        {
            if (m_currentlyCapturing)
                return;

            var videoInputFlags = _BMDVideoInputFlags.bmdVideoInputFlagDefault;

            m_applyDetectedInputMode = applyDetectedInputMode;
            m_validInputSignal = false;

            // Enable input video mode detection if the device supports it
            if (supportsFormatDetection && m_applyDetectedInputMode)
                videoInputFlags |= _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection;

            // Set the screen preview
            m_deckLinkInput.SetScreenPreviewCallback(screenPreviewCallback);

            // Set capture callback
            m_deckLinkInput.SetCallback(this);

            // Set the video input mode
            //For upload to GPU we need 8bitYUV
            m_deckLinkInput.EnableVideoInput(displayMode.GetDisplayMode(), _BMDPixelFormat.bmdFormat8BitYUV, videoInputFlags);
            m_deckLinkInput.EnableAudioInput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 16);

            // Start the capture
            m_deckLinkInput.StartStreams();

            m_currentlyCapturing = true;
        }

        public void StopCapture()
        {
            if (!m_currentlyCapturing)
                return;

            RemoveAllListeners();

            // Stop the capture
            m_deckLinkInput.StopStreams();

            // disable callbacks
            m_deckLinkInput.SetScreenPreviewCallback(null);
            m_deckLinkInput.SetCallback(null);

            m_deckLinkInput.DisableVideoInput();
            m_deckLinkInput.DisableAudioInput();

            m_currentlyCapturing = false;

            for (int i = m_memSize - 1; i >= 0; i--)
            {
                if (m_videoMem[i] != null)
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(m_videoMem[i]);
                    m_videoMem[i] = null;
                }
                if (m_audioMem[i] != null)
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(m_audioMem[i]);
                    m_audioMem[i] = null;
                }
            }
            m_lastUsedVideoMem = -1;
            m_lastUsedAudioMem = -1;
            m_frameNo = -1;
            m_lastFrameNo = -1;

        }

        void RemoveAllListeners()
        {
            InputSignalChanged = null;
            InputFormatChanged = null;
        }
    }

    public delegate void DeckLinkVideoOutputHandler(bool prerolling);
    public delegate void DeckLinkPlaybackStoppedHandler();
    public delegate void DeckLinkAudioOutputHandler();
    public class DeckLinkOutputDevice : IDeckLinkVideoOutputCallback, IDeckLinkAudioOutputCallback, IEnumerable<IDeckLinkDisplayMode>
    {
        private IDeckLink m_deckLink;
        private IDeckLinkOutput m_deckLinkOutput;

        public DeckLinkOutputDevice(IDeckLink deckLink)
        {
            m_deckLink = deckLink;

            // Get output interface
            try
            {
                m_deckLinkOutput = (IDeckLinkOutput)m_deckLink;
            }
            catch (InvalidCastException)
            {
                // No output interface found, eg in case of DeckLink Mini Recorder
                return;
            }

            // Provide the delegate to the audio and video output interfaces
            m_deckLinkOutput.SetScheduledFrameCompletionCallback(this);
            m_deckLinkOutput.SetAudioCallback(this);
        }

        public event DeckLinkVideoOutputHandler VideoFrameCompleted;
        public event DeckLinkPlaybackStoppedHandler PlaybackStopped;
        public event DeckLinkAudioOutputHandler AudioOutputRequested;

        public IDeckLink deckLink
        {
            get { return m_deckLink; }
        }

        public IDeckLinkOutput deckLinkOutput
        {
            get { return m_deckLinkOutput; }
        }

        public string deviceName
        {
            get
            {
                string deviceName;
                m_deckLink.GetDisplayName(out deviceName);
                return deviceName;
            }
        }

        public bool hasGenlock
        {
            get
            {
                int flag;
                var deckLinkAttributes = (IDeckLinkProfileAttributes)m_deckLink;
                deckLinkAttributes.GetFlag(_BMDDeckLinkAttributeID.BMDDeckLinkHasReferenceInput, out flag);
                return flag != 0;
            }
        }

        public bool hasFullGenlockOffset
        {
            get
            {
                int flag;
                var deckLinkAttributes = (IDeckLinkProfileAttributes)m_deckLink;
                deckLinkAttributes.GetFlag(_BMDDeckLinkAttributeID.BMDDeckLinkSupportsFullFrameReferenceInputTimingOffset, out flag);
                return flag != 0;
            }
        }

        public bool isGenlockLocked
        {
            get
            {
                int flag;
                var deckLinkStatus = (IDeckLinkStatus)m_deckLink;
                deckLinkStatus.GetFlag(_BMDDeckLinkStatusID.bmdDeckLinkStatusReferenceSignalLocked, out flag);
                return flag != 0;
            }
        }

        public int getGenlockSignalFlags
        {
            get
            {
                int flag;
                var deckLinkStatus = (IDeckLinkStatus)m_deckLink;
                try
                {
                    deckLinkStatus.GetFlag(_BMDDeckLinkStatusID.bmdDeckLinkStatusReferenceSignalFlags, out flag);
                }
                catch
                {
                    return -1;
                }
                return flag;
            }
        }

        public int getGenlockSignalMode
        {
            get
            {
                int flag;
                var deckLinkStatus = (IDeckLinkStatus)m_deckLink;
                try
                {
                    deckLinkStatus.GetFlag(_BMDDeckLinkStatusID.bmdDeckLinkStatusReferenceSignalMode, out flag);
                }
                catch
                {
                    return -1;
                }
                return flag;
            }
        }

        public void RemoveAllListeners()
        {
            AudioOutputRequested = null;
            PlaybackStopped = null;
            VideoFrameCompleted = null;
        }

        IEnumerator<IDeckLinkDisplayMode> IEnumerable<IDeckLinkDisplayMode>.GetEnumerator()
        {
            IDeckLinkDisplayModeIterator displayModeIterator;
            m_deckLinkOutput.GetDisplayModeIterator(out displayModeIterator);
            return new DisplayModeEnum(displayModeIterator);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            throw new InvalidOperationException();
        }

        #region callbacks
        // Explicit implementation of IDeckLinkVideoOutputCallback and IDeckLinkAudioOutputCallback
        void IDeckLinkVideoOutputCallback.ScheduledFrameCompleted(IDeckLinkVideoFrame completedFrame, _BMDOutputFrameCompletionResult result)
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
       //     Thread.CurrentThread.Name = "Scheduling+RenderingLoop";
            // m_deckLinkOutput.GetFrameCompletionReferenceTimestamp(completedFrame, 1000, out long timestamp);
            // When a video frame has been completed, generate event to schedule next frame
            VideoFrameCompleted(false);
        }

        void IDeckLinkVideoOutputCallback.ScheduledPlaybackHasStopped()
        {
            PlaybackStopped();
        }

        void IDeckLinkAudioOutputCallback.RenderAudioSamples(int preroll) //called at 50Hz during playback
        {
            // Provide further audio samples to the DeckLink API until our preferred buffer waterlevel is reached
            AudioOutputRequested();

            if (preroll != 0)
            {
                m_deckLinkOutput.StartScheduledPlayback(0, 100, 1.0);
            }
        }
        #endregion
    }


    class DisplayModeEnum : IEnumerator<IDeckLinkDisplayMode>
    {
        private IDeckLinkDisplayModeIterator m_displayModeIterator;
        private IDeckLinkDisplayMode m_displayMode;

        public DisplayModeEnum(IDeckLinkDisplayModeIterator displayModeIterator)
        {
            m_displayModeIterator = displayModeIterator;
        }

        IDeckLinkDisplayMode IEnumerator<IDeckLinkDisplayMode>.Current
        {
            get { return m_displayMode; }
        }

        bool System.Collections.IEnumerator.MoveNext()
        {
            m_displayModeIterator.Next(out m_displayMode);
            return m_displayMode != null;
        }

        void IDisposable.Dispose()
        {
        }

        object System.Collections.IEnumerator.Current
        {
            get { return m_displayMode; }
        }

        void System.Collections.IEnumerator.Reset()
        {
            throw new InvalidOperationException();
        }
    }

    public delegate void DeckLinkDiscoveryHandler(IDeckLink decklinkDevice);
    class DeckLinkDeviceDiscovery : IDeckLinkDeviceNotificationCallback
    {
        private IDeckLinkDiscovery      m_deckLinkDiscovery;

        public event DeckLinkDiscoveryHandler DeviceArrived;
        public event DeckLinkDiscoveryHandler DeviceRemoved;

        public DeckLinkDeviceDiscovery()
        {
            m_deckLinkDiscovery = new CDeckLinkDiscovery();
        }
        ~DeckLinkDeviceDiscovery()
        {
            Disable();
        }

        public void Enable()
        {
            m_deckLinkDiscovery.InstallDeviceNotifications(this);
        }
        public void Disable()
        {
            m_deckLinkDiscovery.UninstallDeviceNotifications();
        }

        void IDeckLinkDeviceNotificationCallback.DeckLinkDeviceArrived(IDeckLink deckLinkDevice)
        {
            DeviceArrived(deckLinkDevice);
        }

        void IDeckLinkDeviceNotificationCallback.DeckLinkDeviceRemoved(IDeckLink deckLinkDevice)
        {
            DeviceRemoved(deckLinkDevice);
        }
    }
}
