using PSXSharp.Core;
using System;
using static PSXSharp.DMAChannel;

namespace PSXSharp {
    public unsafe partial class BUS {
        private void DMAIRQ() => IRQ_CONTROL.IRQsignal(3);

        private void HandleDMA(DMAChannel channel) {
            uint channelNumber = channel.ChannelPort;
            uint step = (uint)(channel.TransferStep == StepAmount.Increment ? 4 : -4);
            uint baseAddress = channel.GetBaseAddress();
            uint transferSize = channel.GetTransferSize();
            DirectionType direction = channel.TransferDirection;

            if (channel.TransferSync != SyncType.LinkedList) {
                DMAChannelHandlers[channelNumber](baseAddress, transferSize, step, direction);
            } else {
                //Only GPU can use LinkedList..?
                if (channelNumber != DMA.GPU_CHANNEL) {
                    throw new Exception($"LinkedList on DAM channel {channelNumber}");
                }

                HandleGPULinkedList(baseAddress);
            }

            channel.Done();

            //DMA IRQ 
            if (DMA.IsIRQEnabled(channelNumber)) {
                DMA.SetIRQ(channelNumber);
            }

            if (DMA.HasIRQ) {
                //Instant IRQ may cause problems
                Scheduler.ScheduleEvent(0, Callback, Event.DMA);
            }
        }

        private void HandleMDECIn(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            //This transfer should always be from RAM
            if (direction != DirectionType.FromRam) {
                throw new Exception("HandleMDECIn transfer to RAM");
            }

            uint data;
            uint currentAddress;
            while (transferSize > 0) {
                currentAddress = baseAddress & 0x1FFFFC;
                data = RAM.Read<uint>(currentAddress);
                MDEC.CommandAndParameters(data);
                baseAddress += step;
                transferSize--;
            }
        }

        private void HandleMDECOut(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            //This transfer should always be to RAM
            if (direction != DirectionType.ToRam) {
                throw new Exception("MDECOut transfer from RAM");
            }

            uint data;
            uint currentAddress;
            while (transferSize > 0) {
                currentAddress = baseAddress & 0x1FFFFC;
                data = MDEC.ReadCurrentMacroblock();
                RAM.Write<uint>(currentAddress, data);
                baseAddress += step;
                transferSize--;
            }
        }

        private void HandleGPUDMA(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            uint data;
            uint currentAddress;
            if (direction == DirectionType.ToRam) {
                GPUToRam();
            } else {
                RamToGPU();
            }

            void GPUToRam() {
                while (transferSize > 0) {
                    currentAddress = baseAddress & 0x1FFFFC;
                    data = GPU.CurrentTransfare.ReadWord();
                    RAM.Write<uint>(currentAddress, data);
                    if (GPU.CurrentTransfare.DataEnd) {
                        GPU.CurrentTransfare = null;
                        GPU.currentState = GPU.GPUState.Idle;
                    }
                    baseAddress += step;
                    transferSize--;
                }
            }

            void RamToGPU() {
                while (transferSize > 0) {
                    currentAddress = baseAddress & 0x1FFFFC;
                    data = RAM.Read<uint>(currentAddress);
                    GPU.WriteGP0(data);
                    baseAddress += step;
                    transferSize--;
                }
            }
        }

        private void HandleGPULinkedList(uint baseAddress) {
            //GPU channel Linked List
            //A hacky way to prevent infinite lists is to limit the transfer to 0xFFFF
            const int MAX_TRANSFER_COUNT = 0xFFFF;
            int transferCount = 0;
            uint address = baseAddress & 0x1FFFFC;

            while (transferCount < MAX_TRANSFER_COUNT) {
                //First word contains how many words to transfer and the address of the next node
                uint header = RAM.Read<uint>(address);
                uint wordsCount = header >> 24;

                while (wordsCount-- > 0) {
                    address = (address + 4) & 0x1FFFFC;
                    uint command = RAM.Read<uint>(address);
                    GPU.WriteGP0(command);
                }

                //Stop if bit 23 is set
                if ((header & 0x800000) != 0) {
                    break;
                }

                //Get the address of the next node
                address = header & 0x1FFFFC;
                transferCount++;
            }
        }

        private void HandleCDROMDMA(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            //This transfer should always be to RAM
            if (direction != DirectionType.ToRam) {
                throw new Exception("CDROM transfer from RAM");
            }

            uint data;
            uint currentAddress;
            while (transferSize > 0) {
                currentAddress = baseAddress & 0x1FFFFC;
                data = CDROM.DataController.ReadWord();
                RAM.Write<uint>(currentAddress, data);
                baseAddress += step;
                transferSize--;
            }
        }

        private void HandleSPUDMA(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            uint data;
            uint currentAddress;
            if (direction == DirectionType.ToRam) {
                SPUToRam();
            } else {
                RamToSPU();
            }

            void SPUToRam() {
                while (transferSize > 0) {
                    currentAddress = baseAddress & 0x1FFFFC;
                    data = SPU.ReadDMA();
                    RAM.Write<uint>(currentAddress, data);
                    baseAddress += step;
                    transferSize--;
                }
            }

            void RamToSPU() {
                while (transferSize > 0) {
                    currentAddress = baseAddress & 0x1FFFFC;
                    data = RAM.Read<uint>(currentAddress);
                    SPU.WriteDMA(data);
                    baseAddress += step;
                    transferSize--;
                }
            }
        }

        private void HandlePIODMA(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            throw new Exception("Unimplemented PIO DMA");
        }

        private void HandleOTCDMA(uint baseAddress, uint transferSize, uint step, DirectionType direction) {
            //This transfer should always be to RAM
            if (direction != DirectionType.ToRam) {
                throw new Exception("OTC transfer from RAM");
            }

            uint data;
            uint currentAddress;
            while (transferSize > 0) {
                currentAddress = baseAddress & 0x1FFFFC;
                data = transferSize == 1 ? 0xFFFFFF : (baseAddress - 4) & 0x1FFFFF;
                RAM.Write<uint>(currentAddress, data);
                baseAddress += step;
                transferSize--;
            }
        }
    }
}
