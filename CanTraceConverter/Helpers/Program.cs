/****************************************************************************

  (c) SYS TEC electronic AG, D-08468 Heinsdorfergrund, Am Windrad 2
      www.systec-electronic.com

  Project:      USB-CANmodul

  Description:  Copnverter tool to convert the binary CAN trace file into
                text format.

  -------------------------------------------------------------------------

                $RCSfile:$

                $Author:$

                $Revision:$  $Date:$

                $State:$

                Build Environment:
                    ...

  -------------------------------------------------------------------------

  Revision History:

  05-feb-2015 r.d.: - first implementation

****************************************************************************/

using System;
using System.IO;

//using USBcanServer;

namespace CanTraceConverter.Helpers
{
    /*class ConvertUcanTraceProgram
    {
        static bool fInputFileSet_l = false;
        static string strInputFilePath_l;
        static bool fOutputFileSet_l = false;
        static string strOutputFilePath_l;
        static bool fPrintToStdOut_l = false;
        static bool fPrintDiffTime_l = false;

        /// <summary>
        /// Main entry point oth the converter tool.
        /// </summary>
        /// <param name="args">Argument string array passed to the tool.</param>
        static void Main(string[] args)
        {
            Console.WriteLine("ConvertUcanTrace - by r.d. SYS TEC electronic AG");
            Console.WriteLine();

            // parse the command line arguments
            if (ParseCommandLines(args) == false)
            {
                goto Exit;
            }

            int nRes;
            // convert the binary trace file
            if ((nRes = ConvertCanTraceFile()) != 0)
            {
                Console.WriteLine("Conversion UNSUCCESSFUL! Error code = {0:X4}h", nRes);
                goto Exit;
            }

            Console.WriteLine("Conversion successful.");

        Exit:
            Console.WriteLine();
        }

        /// <summary>
        /// Parses all command line parameters and checks them for validity.
        /// </summary>
        /// <param name="args">Argument string array passed to the tool.</param>
        /// <returns>
        /// Boolean - true if the command line parameters were parsed successfully.
        /// </returns>
        static bool ParseCommandLines(string[] args)
        {
            bool fRet = false;

            // check if no parameters re passed
            if (args.Length < 1)
            {
                Console.WriteLine("Error: No arguments found.");
                PrintHelp();
                goto Exit;
            }

            // read and check all parameters
            for (int i = 0; i < args.Length; i++)
            {
                // convert the current parameter to lower cases
                string strArg = args[i].ToLower();

                // check if the parameter is an option
                if (strArg.IndexOf("--") == 0)
                {
                    if (strArg.IndexOf("help") == 2)
                    {
                        PrintHelp();
                        goto Exit;
                    }
                    else if (strArg.IndexOf("stdout") == 2)
                    {
                        fPrintToStdOut_l = true;
                    }
                    else if (strArg.IndexOf("diff") == 2)
                    {
                        fPrintDiffTime_l = true;
                    }
                }
                else
                {
                    // the parameter is not an option

                    // the first always is the input file path
                    if (fInputFileSet_l == false)
                    {
                        strInputFilePath_l = strArg;
                        fInputFileSet_l = true;
                    }

                    // the second always is the output file path
                    else if (fOutputFileSet_l == false)
                    {
                        strOutputFilePath_l = strArg;
                        fOutputFileSet_l = true;
                    }

                    else
                    {
                        // too many parameters which are not options
                        Console.WriteLine("Error: More than two file pathes specified.");
                        PrintHelp();
                        goto Exit;
                    }
                }
            }

            // the input file is mandatory
            if (fInputFileSet_l == false)
            {
                Console.WriteLine("Error: No input file path specified.");
                PrintHelp();
                goto Exit;
            }

            // optionally the output file is automatically set by patching the extension
            if ((fOutputFileSet_l == false) && (fPrintToStdOut_l == false))
            {
                // copy the input file path to the output file path
                strOutputFilePath_l = strInputFilePath_l;

                // try to find tle extension and remove it
                int nPos = strOutputFilePath_l.LastIndexOf ('.');
                if (nPos != -1)
                {
                    strOutputFilePath_l = strOutputFilePath_l.Substring(0, nPos);
                }

                // append the new extension
                strOutputFilePath_l += ".txt";
                Console.WriteLine("Write to file '{0}'", strOutputFilePath_l);
            }

            fRet = true;
        Exit:
            return fRet;
        }

        /// <summary>
        /// Prints the help information to the console.
        /// </summary>
        static void PrintHelp()
        {
            Console.WriteLine("USAGE: ConvertUcanTrace.exe <input-file> [<output-file>] [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --help     Displays this help.");
            Console.WriteLine("  --stdout   Prints the converted information to the StdOut.");
            Console.WriteLine("  --diff     Time stamp is converted to differential time to prvious");
            Console.WriteLine("             message.");
        }

        /// <summary>
        /// Converts a Binary Trace File created by PCANView (USBCAN).
        /// </summary>
        /// <returns>
        /// Error code - zero for successful.
        /// </returns>
        static int ConvertCanTraceFile()
        {
            int nRet = 0;
            int nCount = 0;
            System.IO.StreamWriter OutputFile = null;

            // check if the input file does exist
            if (File.Exists(strInputFilePath_l))
            {
                // check if CAN messages has to be written to a file instead to the console
                if (fPrintToStdOut_l == false)
                {
                    try
                    {
                        // try to create the output file
                        OutputFile = new System.IO.StreamWriter(strOutputFilePath_l);
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine("Error: Could not create output file '{0}'\n{1}.", strOutputFilePath_l, ex.Message);
                        nRet = 0x1001;
                        goto Exit;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error: Could not create output file '{0}\n{1}'.", strOutputFilePath_l, ex.Message);
                        nRet = 0x1001;
                        goto Exit;
                    }
                }

                // open input file and convert the whole content
                using (UcanTraceBinaryReader InputReader = new UcanTraceBinaryReader(File.Open(strInputFilePath_l, FileMode.Open)))
                {
                    // Description of the binary logfile created by PCANView (USBCAN) since PeakUcan.dll V5.09.
                    //
                    // Offset #======== File Header =======================================#
                    // 0x0000 # Signature 0x5543414E (32 bit)                              #
                    //        #------------------------------------------------------------#
                    // 0x0004 # Offset for beginning of the CAN messages (32 bit)          #
                    //        #------------------------------------------------------------#
                    // 0x0008 # Version code for PeakUcan.dll (32 bit)                     #
                    //        #------------------------------------------------------------#
                    // 0x000C # Creation date and time  (see structure SYSTEMTIME)         #
                    //        #------------------------------------------------------------#
                    // 0x001C # OS version (see structure OSVERSIONINFO)                   #
                    //        #------------------------------------------------------------#
                    // 0x00B0 # Hardware information (see structure tUcanHardwareInfoEx)   #
                    //        #------------------------------------------------------------#
                    // 0x00D6 # CAN channel 0 information (see structure tUcanChannelInfo) #
                    //        #------------------------------------------------------------#
                    // 0x00F0 # CAN channel 1 information (see structure tUcanChannelInfo) #
                    //        #======== CAN messages ======================================#
                    // 0x010A # CAN message 1 (see structure tCanMsgStruct)                #
                    //        #------------------------------------------------------------#
                    // 0x011C # CAN message 2 (see structure tCanMsgStruct)                #
                    //        #------------------------------------------------------------#
                    // ...    # ...                                                        #
                    //        #======== End Of File =======================================#

                    UInt32 dwSignature, dwOffset, dwVersion;
                    try
                    {
                        // read the file signature and check if valid
                        dwSignature = InputReader.ReadUInt32();
                        if (dwSignature != 0x5543414E)
                        {
                            Console.WriteLine("Error: Invalid binary trace file.");
                            nRet = 0x1002;
                            goto Exit;
                        }

                        // read the offset to locate the start position of the CAN messages
                        dwOffset = InputReader.ReadUInt32();
                        if (dwOffset > InputReader.BaseStream.Length)
                        {
                            Console.WriteLine("Error: Invalid binary trace file.");
                            nRet = 0x1003;
                            goto Exit;
                        }

                        // read header from input file
                        dwVersion = InputReader.ReadUInt32();
                        SYSTEMTIME SystemTime = InputReader.ReadSystemTime();
                        OSVERSIONINFO OsVersion = InputReader.ReadOsVersion();
                        UcanDotNET.USBcanServer.tUcanHardwareInfoEx UcanHwInfo = InputReader.ReadUcanHwInfo();
                        UcanDotNET.USBcanServer.tUcanChannelInfo CanInfoCh0 = InputReader.ReadUcanChannelInfo();
                        UcanDotNET.USBcanServer.tUcanChannelInfo CanInfoCh1 = InputReader.ReadUcanChannelInfo();

                        if (fPrintToStdOut_l)
                        {
                            // write header to console
                            Console.WriteLine("InputFile: {0}", strInputFilePath_l);
                            Console.WriteLine("Version:   {0}", String.Format(new VersionFormatProvider(), "PeakUcan.dll {0}", dwVersion));
                            Console.WriteLine("Date/Time: {0}", String.Format(new SystemTimeFormatProvider(), "{0}", SystemTime));
                            Console.WriteLine("OS:        {0}", String.Format(new OsVerisonFormatProvider(), "{0}", OsVersion));
                            Console.WriteLine("Hardware:  {0}", String.Format(new UcanHwInfoFormatProvider(), "{0}", UcanHwInfo));
                            Console.WriteLine("Channel0:  {0}", String.Format(new UcanChannelInfoFormatProvider(), "{0}", CanInfoCh0));
                            Console.WriteLine("Channel1:  {0}", String.Format(new UcanChannelInfoFormatProvider(), "{0}", CanInfoCh1));
                            Console.WriteLine(UcanMsgFormatProvider.GetMessageHeader());
                        }
                        else
                        {
                            // write header to output file
                            OutputFile.WriteLine("InputFile: {0}", strInputFilePath_l);
                            OutputFile.WriteLine("Version:   {0}", String.Format(new VersionFormatProvider(), "PeakUcan.dll {0}", dwVersion));
                            OutputFile.WriteLine("Date/Time: {0}", String.Format(new SystemTimeFormatProvider(), "{0}", SystemTime));
                            OutputFile.WriteLine("OS:        {0}", String.Format(new OsVerisonFormatProvider(), "{0}", OsVersion));
                            OutputFile.WriteLine("Hardware:  {0}", String.Format(new UcanHwInfoFormatProvider(), "{0}", UcanHwInfo));
                            OutputFile.WriteLine("Channel0:  {0}", String.Format(new UcanChannelInfoFormatProvider(), "{0}", CanInfoCh0));
                            OutputFile.WriteLine("Channel1:  {0}", String.Format(new UcanChannelInfoFormatProvider(), "{0}", CanInfoCh1));
                            OutputFile.WriteLine(UcanMsgFormatProvider.GetMessageHeader());
                        }

                        // get the active CAN channel information - is needed for UcanMsgFormatProvider
                        UcanDotNET.USBcanServer.tUcanChannelInfo CanInfoCh;
                        CanInfoCh = (CanInfoCh0.m_fCanIsInit) ? CanInfoCh0 : CanInfoCh1;

                        // create a new instance of a CAN message for converting the time stamp to a differntial time
                        UcanDotNET.USBcanServer.tCanMsgStruct PrevCanMsg = UcanDotNET.USBcanServer.tCanMsgStruct.CreateInstance(0,0);

                        // loop to read and convert all CAN messages
                        for (nCount = 0; ; nCount++)
                        {
                            UcanDotNET.USBcanServer.tCanMsgStruct CanMsg = InputReader.ReadUcanMessage();

                            // check if time stamp has to be converted to differential time
                            if (fPrintDiffTime_l)
                            {
                                // convert time stamp to differential time
                                int nTime = CanMsg.m_dwTime;
                                if (nCount > 0)
                                {
                                    nTime -= PrevCanMsg.m_dwTime;
                                }
                                PrevCanMsg = CanMsg;
                                CanMsg.m_dwTime = nTime;
                            }

                            if (fPrintToStdOut_l)
                            {
                                // write CAN message to console
                                Console.WriteLine("{0}", String.Format(new UcanMsgFormatProvider(UcanHwInfo, CanInfoCh), "{0}", CanMsg));
                            }
                            else
                            {
                                // write CAN message to output file
                                OutputFile.WriteLine("{0}", String.Format(new UcanMsgFormatProvider(UcanHwInfo, CanInfoCh), "{0}", CanMsg));
                            }
                        }
                    }
                    catch (EndOfStreamException ex)
                    {
                        // dummy to prevent compiler warning
                        string strDummy = ex.Message;
                        strDummy = strDummy.Length.ToString();
                        if (fPrintToStdOut_l)
                        {
                            // write total number of CAN messages to console
                            Console.WriteLine("Total number of CAN messages: {0}", nCount);
                        }
                        else
                        {
                            // write total number of CAN messages to output file
                            OutputFile.WriteLine("Total number of CAN messages: {0}", nCount);
                            OutputFile.Close();
                        }
                        InputReader.Close();
                    }
                    catch (IOException ex)
                    {
                        // IO Exception
                        Console.WriteLine("Error: File IO exception:\n{0}", ex.Message);
                        if (fPrintToStdOut_l == false)
                        {
                            OutputFile.Close();
                        }
                        InputReader.Close();
                        nRet = 0x7FFF;
                        goto Exit;
                    }
                }
            }
            else
            {
                // input file was not found
                Console.WriteLine("Error: Input file does not exist.");
                nRet = 0x1000;
                goto Exit;
            }

        Exit:
            return nRet;
        }
    }

    #region Special Structures

    #endregion

    #region Class UcanTraceBinaryReader

    #endregion

    #region Format Provider Classes

    #endregion*/

}


// EOF
