﻿/*
* Copyright 2007 ZXing authors
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
*      http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/
using System;
using System.Text;
using System.Collections;
using com.google.zxing;
using com.google.zxing.common;
using com.google.zxing.common.reedsolomon;
using com.google.zxing.qrcode.decoder;
using com.google.zxing.qrcode;

namespace com.google.zxing.qrcode.encoder
{
    using Version=com.google.zxing.qrcode.decoder.Version;   
    public sealed class Encoder
    { 
         // The original table is defined in the table 5 of JISX0510:2004 (p.19).
          private static int[] ALPHANUMERIC_TABLE = {
              -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,  // 0x00-0x0f
              -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,  // 0x10-0x1f
              36, -1, -1, -1, 37, 38, -1, -1, -1, -1, 39, 40, -1, 41, 42, 43,  // 0x20-0x2f
              0,   1,  2,  3,  4,  5,  6,  7,  8,  9, 44, -1, -1, -1, -1, -1,  // 0x30-0x3f
              -1, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,  // 0x40-0x4f
              25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, -1, -1, -1, -1, -1,  // 0x50-0x5f
          };

          private Encoder() {
          }

          // The mask penalty calculation is complicated.  See Table 21 of JISX0510:2004 (p.45) for details.
          // Basically it applies four rules and summate all penalties.
          private static int calculateMaskPenalty(ByteMatrix matrix) {
            int penalty = 0;
            penalty += MaskUtil.applyMaskPenaltyRule1(matrix);
            penalty += MaskUtil.applyMaskPenaltyRule2(matrix);
            penalty += MaskUtil.applyMaskPenaltyRule3(matrix);
            penalty += MaskUtil.applyMaskPenaltyRule4(matrix);
            return penalty;
          }

          private class BlockPair {

            private ByteArray dataBytes;
            private ByteArray errorCorrectionBytes;

            public BlockPair(ByteArray data, ByteArray errorCorrection) {
              dataBytes = data;
              errorCorrectionBytes = errorCorrection;
            }

            public ByteArray getDataBytes() {
              return dataBytes;
            }

            public ByteArray getErrorCorrectionBytes() {
              return errorCorrectionBytes;
            }

          }

          // Encode "bytes" with the error correction level "getECLevel". The encoding mode will be chosen
          // internally by chooseMode(). On success, store the result in "qrCode" and return true.
          // We recommend you to use QRCode.EC_LEVEL_L (the lowest level) for
          // "getECLevel" since our primary use is to show QR code on desktop screens. We don't need very
          // strong error correction for this purpose.
          //
          // Note that there is no way to encode bytes in MODE_KANJI. We might want to add EncodeWithMode()
          // with which clients can specify the encoding mode. For now, we don't need the functionality.
          public static void encode(String content, ErrorCorrectionLevel ecLevel, QRCode qrCode)
          {
            // Step 1: Choose the mode (encoding).
            Mode mode = chooseMode(content);

            // Step 2: Append "bytes" into "dataBits" in appropriate encoding.
            BitVector dataBits = new BitVector();
            appendBytes(content, mode, dataBits);
            // Step 3: Initialize QR code that can contain "dataBits".
            int numInputBytes = dataBits.sizeInBytes();
            initQRCode(numInputBytes, ecLevel, mode, qrCode);

            // Step 4: Build another bit vector that contains header and data.
            BitVector headerAndDataBits = new BitVector();
            appendModeInfo(qrCode.getMode(), headerAndDataBits);
            appendLengthInfo(content.Length, qrCode.getVersion(), qrCode.getMode(), headerAndDataBits);
            headerAndDataBits.appendBitVector(dataBits);

            // Step 5: Terminate the bits properly.
            terminateBits(qrCode.getNumDataBytes(), headerAndDataBits);

            // Step 6: Interleave data bits with error correction code.
            BitVector finalBits = new BitVector();
            interleaveWithECBytes(headerAndDataBits, qrCode.getNumTotalBytes(), qrCode.getNumDataBytes(),
                qrCode.getNumRSBlocks(), finalBits);

            // Step 7: Choose the mask pattern and set to "qrCode".
            ByteMatrix matrix = new ByteMatrix(qrCode.getMatrixWidth(), qrCode.getMatrixWidth());
            qrCode.setMaskPattern(chooseMaskPattern(finalBits, qrCode.getECLevel(), qrCode.getVersion(),
                matrix));

            // Step 8.  Build the matrix and set it to "qrCode".
            MatrixUtil.buildMatrix(finalBits, qrCode.getECLevel(), qrCode.getVersion(),
                qrCode.getMaskPattern(), matrix);
            qrCode.setMatrix(matrix);
            // Step 9.  Make sure we have a valid QR Code.
            if (!qrCode.isValid()) {
              throw new WriterException("Invalid QR code: " + qrCode.toString());
            }
          }

          // Return the code point of the table used in alphanumeric mode. Return -1 if there is no
          // corresponding code in the table.
          static int getAlphanumericCode(int code) {
            if (code < ALPHANUMERIC_TABLE.Length) {
              return ALPHANUMERIC_TABLE[code];
            }
            return -1;
          }

          // Choose the best mode by examining the content.
          //
          // Note that this function does not return MODE_KANJI, as we cannot distinguish Shift_JIS from
          // other encodings such as ISO-8859-1, from data bytes alone. For example "\xE0\xE0" can be
          // interpreted as one character in Shift_JIS, but also two characters in ISO-8859-1.
          //
          // JAVAPORT: This MODE_KANJI limitation sounds like a problem for us.
          public static Mode chooseMode(String content) {
            bool hasNumeric = false;
            bool hasAlphanumeric = false;
            for (int i = 0; i < content.Length; ++i) {
              char c = content[i];
              if (c >= '0' && c <= '9') {
                hasNumeric = true;
              } else if (getAlphanumericCode(c) != -1) {
                hasAlphanumeric = true;
              } else {
                return Mode.BYTE;
              }
            }
            if (hasAlphanumeric) {
              return Mode.ALPHANUMERIC;
            } else if (hasNumeric) {
              return Mode.NUMERIC;
            }
            return Mode.BYTE;
          }

          private static int chooseMaskPattern(BitVector bits, ErrorCorrectionLevel ecLevel, int version,ByteMatrix matrix){
              try{
                  int minPenalty = int.MaxValue;  // Lower penalty is better.
                  int bestMaskPattern = -1;
                  // We try all mask patterns to choose the best one.
                  for (int maskPattern = 0; maskPattern < QRCode.NUM_MASK_PATTERNS; maskPattern++)
                  {
                      MatrixUtil.buildMatrix(bits, ecLevel, version, maskPattern, matrix);
                      int penalty = calculateMaskPenalty(matrix);
                      if (penalty < minPenalty)
                      {
                          minPenalty = penalty;
                          bestMaskPattern = maskPattern;
                      }
                  }
                  return bestMaskPattern;              
              }catch(Exception e){
                  throw new ReaderException(e.Message);
              }            
          }

          // Initialize "qrCode" according to "numInputBytes", "ecLevel", and "mode". On success, modify
          // "qrCode".
          private static void initQRCode(int numInputBytes, ErrorCorrectionLevel ecLevel, Mode mode, QRCode qrCode)
          {
              try
              {
                qrCode.setECLevel(ecLevel);
                qrCode.setMode(mode);

                // In the following comments, we use numbers of Version 7-H.
                for (int versionNum = 1; versionNum <= 40; versionNum++) {
                  Version version = Version.getVersionForNumber(versionNum);
                  // numBytes = 196
                  int numBytes = version.getTotalCodewords();
                  // getNumECBytes = 130
                  Version.ECBlocks ecBlocks = version.getECBlocksForLevel(ecLevel);
                  int numEcBytes = ecBlocks.getTotalECCodewords();
                  // getNumRSBlocks = 5
                  int numRSBlocks = ecBlocks.getNumBlocks();
                  // getNumDataBytes = 196 - 130 = 66
                  int numDataBytes = numBytes - numEcBytes;
                  // We want to choose the smallest version which can contain data of "numInputBytes" + some
                  // extra bits for the header (mode info and length info). The header can be three bytes
                  // (precisely 4 + 16 bits) at most. Hence we do +3 here.
                  if (numDataBytes >= numInputBytes + 3) {
                    // Yay, we found the proper rs block info!
                    qrCode.setVersion(versionNum);
                    qrCode.setNumTotalBytes(numBytes);
                    qrCode.setNumDataBytes(numDataBytes);
                    qrCode.setNumRSBlocks(numRSBlocks);
                    // getNumECBytes = 196 - 66 = 130
                    qrCode.setNumECBytes(numEcBytes);
                    // matrix width = 21 + 6 * 4 = 45
                    qrCode.setMatrixWidth(version.getDimensionForVersion());
                    return;
                  }
                }
                throw new WriterException("Cannot find proper rs block info (input data too big?)");
              }
              catch(Exception e){
                throw new WriterException(e.Message);
              }
          }

          // Terminate bits as described in 8.4.8 and 8.4.9 of JISX0510:2004 (p.24).
          static void terminateBits(int numDataBytes, BitVector bits){
            int capacity = numDataBytes << 3;
            if (bits.size() > capacity) {
              throw new WriterException("data bits cannot fit in the QR Code" + bits.size() + " > " + capacity);
            }
            // Append termination bits. See 8.4.8 of JISX0510:2004 (p.24) for details.
            for (int i = 0; i < 4 && bits.size() < capacity; ++i) {
              bits.appendBit(0);
            }
            int numBitsInLastByte = bits.size() % 8;
            // If the last byte isn't 8-bit aligned, we'll add padding bits.
            if (numBitsInLastByte > 0) {
              int numPaddingBits = 8 - numBitsInLastByte;
              for (int i = 0; i < numPaddingBits; ++i) {
                bits.appendBit(0);
              }
            }
            // Should be 8-bit aligned here.
            if (bits.size() % 8 != 0) {
              throw new WriterException("Number of bits is not a multiple of 8");
            }
            // If we have more space, we'll fill the space with padding patterns defined in 8.4.9 (p.24).
            int numPaddingBytes = numDataBytes - bits.sizeInBytes();
            for (int i = 0; i < numPaddingBytes; ++i) {
              if (i % 2 == 0) {
                bits.appendBits(0xec, 8);
              } else {
                bits.appendBits(0x11, 8);
              }
            }
            if (bits.size() != capacity) {
              throw new WriterException("Bits size does not equal capacity");
            }
          }

          // Get number of data bytes and number of error correction bytes for block id "blockID". Store
          // the result in "numDataBytesInBlock", and "numECBytesInBlock". See table 12 in 8.5.1 of
          // JISX0510:2004 (p.30)
          static void getNumDataBytesAndNumECBytesForBlockID(int numTotalBytes, int numDataBytes,
              int numRSBlocks, int blockID, int[] numDataBytesInBlock,int[] numECBytesInBlock)  {
            if (blockID >= numRSBlocks) {
              throw new WriterException("Block ID too large");
            }
            // numRsBlocksInGroup2 = 196 % 5 = 1
            int numRsBlocksInGroup2 = numTotalBytes % numRSBlocks;
            // numRsBlocksInGroup1 = 5 - 1 = 4
            int numRsBlocksInGroup1 = numRSBlocks - numRsBlocksInGroup2;
            // numTotalBytesInGroup1 = 196 / 5 = 39
            int numTotalBytesInGroup1 = numTotalBytes / numRSBlocks;
            // numTotalBytesInGroup2 = 39 + 1 = 40
            int numTotalBytesInGroup2 = numTotalBytesInGroup1 + 1;
            // numDataBytesInGroup1 = 66 / 5 = 13
            int numDataBytesInGroup1 = numDataBytes / numRSBlocks;
            // numDataBytesInGroup2 = 13 + 1 = 14
            int numDataBytesInGroup2 = numDataBytesInGroup1 + 1;
            // numEcBytesInGroup1 = 39 - 13 = 26
            int numEcBytesInGroup1 = numTotalBytesInGroup1 - numDataBytesInGroup1;
            // numEcBytesInGroup2 = 40 - 14 = 26
            int numEcBytesInGroup2 = numTotalBytesInGroup2 - numDataBytesInGroup2;
            // Sanity checks.
            // 26 = 26
            if (numEcBytesInGroup1 != numEcBytesInGroup2) {
              throw new WriterException("EC bytes mismatch");
            }
            // 5 = 4 + 1.
            if (numRSBlocks != numRsBlocksInGroup1 + numRsBlocksInGroup2) {
              throw new WriterException("RS blocks mismatch");
            }
            // 196 = (13 + 26) * 4 + (14 + 26) * 1
            if (numTotalBytes !=
                ((numDataBytesInGroup1 + numEcBytesInGroup1) *
                    numRsBlocksInGroup1) +
                    ((numDataBytesInGroup2 + numEcBytesInGroup2) *
                        numRsBlocksInGroup2)) {
              throw new WriterException("Total bytes mismatch");
            }

            if (blockID < numRsBlocksInGroup1) {
              numDataBytesInBlock[0] = numDataBytesInGroup1;
              numECBytesInBlock[0] = numEcBytesInGroup1;
            } else {
              numDataBytesInBlock[0] = numDataBytesInGroup2;
              numECBytesInBlock[0] = numEcBytesInGroup2;
            }
          }

          // Interleave "bits" with corresponding error correction bytes. On success, store the result in
          // "result" and return true. The interleave rule is complicated. See 8.6
          // of JISX0510:2004 (p.37) for details.
          static void interleaveWithECBytes(BitVector bits, int numTotalBytes,
              int numDataBytes, int numRSBlocks, BitVector result) {

            // "bits" must have "getNumDataBytes" bytes of data.
            if (bits.sizeInBytes() != numDataBytes) {
              throw new WriterException("Number of bits and data bytes does not match");
            }

            // Step 1.  Divide data bytes into blocks and generate error correction bytes for them. We'll
            // store the divided data bytes blocks and error correction bytes blocks into "blocks".
            int dataBytesOffset = 0;
            int maxNumDataBytes = 0;
            int maxNumEcBytes = 0;

            // Since, we know the number of reedsolmon blocks, we can initialize the vector with the number.
            ArrayList blocks = new ArrayList(numRSBlocks);

            for (int i = 0; i < numRSBlocks; ++i) {
              int[] numDataBytesInBlock = new int[1];
              int[] numEcBytesInBlock = new int[1];
              getNumDataBytesAndNumECBytesForBlockID(
                  numTotalBytes, numDataBytes, numRSBlocks, i,
                  numDataBytesInBlock, numEcBytesInBlock);

              ByteArray dataBytes = new ByteArray();
              dataBytes.set(bits.getArray(), dataBytesOffset, numDataBytesInBlock[0]);
              ByteArray ecBytes = generateECBytes(dataBytes, numEcBytesInBlock[0]);
              blocks.Add(new BlockPair(dataBytes, ecBytes));

              maxNumDataBytes = Math.Max(maxNumDataBytes, dataBytes.size());
              maxNumEcBytes = Math.Max(maxNumEcBytes, ecBytes.size());
              dataBytesOffset += numDataBytesInBlock[0];
            }
            if (numDataBytes != dataBytesOffset) {
              throw new WriterException("Data bytes does not match offset");
            }

            // First, place data blocks.
            for (int i = 0; i < maxNumDataBytes; ++i) {
              for (int j = 0; j < blocks.Count; ++j) {
                ByteArray dataBytes = ((BlockPair) blocks[j]).getDataBytes();
                if (i < dataBytes.size()) {
                  result.appendBits(dataBytes.at(i), 8);
                }
              }
            }
            // Then, place error correction blocks.
            for (int i = 0; i < maxNumEcBytes; ++i) {
              for (int j = 0; j < blocks.Count; ++j) {
                ByteArray ecBytes = ((BlockPair) blocks[j]).getErrorCorrectionBytes();
                if (i < ecBytes.size()) {
                  result.appendBits(ecBytes.at(i), 8);
                }
              }
            }
            if (numTotalBytes != result.sizeInBytes()) {  // Should be same.
              throw new WriterException("Interleaving error: " + numTotalBytes + " and " + result.sizeInBytes() +
                " differ.");
            }
          }

          static ByteArray generateECBytes(ByteArray dataBytes, int numEcBytesInBlock) {
            int numDataBytes = dataBytes.size();
            int[] toEncode = new int[numDataBytes + numEcBytesInBlock];
            for (int i = 0; i < numDataBytes; i++) {
              toEncode[i] = dataBytes.at(i);
            }
            new ReedSolomonEncoder(GF256.QR_CODE_FIELD).encode(toEncode, numEcBytesInBlock);

            ByteArray ecBytes = new ByteArray(numEcBytesInBlock);
            for (int i = 0; i < numEcBytesInBlock; i++) {
              ecBytes.set(i, toEncode[numDataBytes + i]);
            }
            return ecBytes;
          }

          // Append mode info. On success, store the result in "bits" and return true. On error, return
          // false.
          static void appendModeInfo(Mode mode, BitVector bits) {
            bits.appendBits(mode.getBits(), 4);
          }


          // Append length info. On success, store the result in "bits" and return true. On error, return
          // false.
          static void appendLengthInfo(int numLetters, int version, Mode mode, BitVector bits){
            int numBits = mode.getCharacterCountBits(Version.getVersionForNumber(version));
            if (numLetters > ((1 << numBits) - 1)) {
              throw new WriterException(numLetters + "is bigger than" + ((1 << numBits) - 1));
            }
            bits.appendBits(numLetters, numBits);
          }

          // Append "bytes" in "mode" mode (encoding) into "bits". On success, store the result in "bits"
          // and return true.
          static void appendBytes(String content, Mode mode, BitVector bits) {
            if (mode.Equals(Mode.NUMERIC)) {
              appendNumericBytes(content, bits);
            } else if (mode.Equals(Mode.ALPHANUMERIC)) {
              appendAlphanumericBytes(content, bits);
            } else if (mode.Equals(Mode.BYTE)) {
              append8BitBytes(content, bits);
            } else if (mode.Equals(Mode.KANJI)) {
              appendKanjiBytes(content, bits);
            } else {
              throw new WriterException("Invalid mode: " + mode);
            }
          }

          static void appendNumericBytes(String content, BitVector bits) {
            int length = content.Length;
            int i = 0;
            while (i < length) {
              int num1 = content[i] - '0';
              if (i + 2 < length) {
                // Encode three numeric letters in ten bits.
                int num2 = content[i + 1] - '0';
                int num3 = content[i + 2] - '0';
                bits.appendBits(num1 * 100 + num2 * 10 + num3, 10);
                i += 3;
              } else if (i + 1 < length) {
                // Encode two numeric letters in seven bits.
                int num2 = content[i + 1] - '0';
                bits.appendBits(num1 * 10 + num2, 7);
                i += 2;
              } else {
                // Encode one numeric letter in four bits.
                bits.appendBits(num1, 4);
                i++;
              }
            }
          }

          static void appendAlphanumericBytes(String content, BitVector bits) {
            int length = content.Length;
            int i = 0;
            while (i < length) {
              int code1 = getAlphanumericCode(content[i]);
              if (code1 == -1) {
                throw new WriterException();
              }
              if (i + 1 < length) {
                int code2 = getAlphanumericCode(content[i + 1]);
                if (code2 == -1) {
                  throw new WriterException();
                }
                // Encode two alphanumeric letters in 11 bits.
                bits.appendBits(code1 * 45 + code2, 11);
                i += 2;
              } else {
                // Encode one alphanumeric letter in six bits.
                bits.appendBits(code1, 6);
                i++;
              }
            }
          }

          static void append8BitBytes(String content, BitVector bits) {
            byte[] bytes;
            try {
                bytes = System.Text.ASCIIEncoding.ASCII.GetBytes("ISO-8859-1");
            } catch (Exception uee) {
              throw new WriterException(uee.ToString());
            }
            for (int i = 0; i < bytes.Length; ++i) {
              bits.appendBits(bytes[i], 8);
            }
          }

          static void appendKanjiBytes(String content, BitVector bits) {
            byte[] bytes;
            try {
                bytes=System.Text.ASCIIEncoding.ASCII.GetBytes("Shift_JIS");
            } catch (Exception uee) {
              throw new WriterException(uee.ToString());
            }
            int length = bytes.Length;
            for (int i = 0; i < length; i += 2) {
              int byte1 = bytes[i] & 0xFF;
              int byte2 = bytes[i + 1] & 0xFF;
              int code = (byte1 << 8) | byte2;
              int subtracted = -1;
              if (code >= 0x8140 && code <= 0x9ffc) {
                subtracted = code - 0x8140;
              } else if (code >= 0xe040 && code <= 0xebbf) {
                subtracted = code - 0xc140;
              }
              if (subtracted == -1) {
                throw new WriterException("Invalid byte sequence");
              }
              int encoded = ((subtracted >> 8) * 0xc0) + (subtracted & 0xff);
              bits.appendBits(encoded, 13);
            }
          }
    
    }
}