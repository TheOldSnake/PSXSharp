namespace PSXSharp.Core.x64_Recompiler {
    using static CPU_x64_Recompiler;
    public static unsafe class AddressGetter {
        public static ulong GetCPUStructAddress() {
            return (ulong)CPU_Struct_Ptr;
        }

        public static ulong GetGPRAddress(int index) {      //index is already masked
            return (ulong)&CPU_Struct_Ptr->GPR[index];
        }

        public static ulong GetPCAddress() {
            return (ulong)&CPU_Struct_Ptr->PC;
        }

        public static ulong GetNextPCAddress() {
            return (ulong)&CPU_Struct_Ptr->Next_PC;
        }

        public static ulong GetCurrentPCAddress() {
            return (ulong)&CPU_Struct_Ptr->Current_PC;
        }

        public static ulong GetHIAddress() {
            return (ulong)&CPU_Struct_Ptr->HI;
        }

        public static ulong GetLOAddress() {
            return (ulong)&CPU_Struct_Ptr->LO;
        }

        public static ulong GetReadyRegisterLoadNumberAddress() {
            return (ulong)&CPU_Struct_Ptr->ReadyLoad.RegisterNumber;
        }

        public static ulong GetReadyRegisterLoadValueAddress() {
            return (ulong)&CPU_Struct_Ptr->ReadyLoad.Value;
        }

        public static ulong GetDelayedRegisterLoadNumberAddress() {
            return (ulong)&CPU_Struct_Ptr->DelayedLoad.RegisterNumber;
        }

        public static ulong GetDelayedRegisterLoadValueAddress() {
            return (ulong)&CPU_Struct_Ptr->DelayedLoad.Value;
        }

        public static ulong GetDirectWriteNumberAddress() {
            return (ulong)&CPU_Struct_Ptr->DirectLoad.RegisterNumber;
        }

        public static ulong GetDirectWriteValueAddress() {
            return (ulong)&CPU_Struct_Ptr->DirectLoad.Value;
        }

        public static ulong GetBranchFlagAddress() {
            return (ulong)&CPU_Struct_Ptr->Branch;
        }

        public static ulong GetDelaySlotAddress() {
            return (ulong)&CPU_Struct_Ptr->DelaySlot;
        }

        public static ulong GetCOP0SRAddress() {
            return (ulong)&CPU_Struct_Ptr->COP0_SR;
        }

        public static ulong GetCOP0CauseAddress() {
            return (ulong)&CPU_Struct_Ptr->COP0_Cause;
        }

        public static ulong GetCOP0EPCAddress() {
            return (ulong)&CPU_Struct_Ptr->COP0_EPC;
        }

        public static ulong GetStubBlockHandlerAddress() {
            delegate* unmanaged[Stdcall]<ulong> ptr = &StubBlockHandler;
            return (ulong)ptr;
        }

        public static ulong GetExceptionAddress() {
            delegate* unmanaged[Stdcall]<CPUNativeStruct*, uint, void> ptr = &ExceptionWrapper;
            return (ulong)ptr;
        }

        public static ulong GetBUSReadByteAddress() {
            delegate* unmanaged[Stdcall]<uint, byte> ptr = &BUSReadByteWrapper;
            return (ulong)ptr;
        }

        public static ulong GetBUSReadHalfAddress() {
            delegate* unmanaged[Stdcall]<uint, ushort> ptr = &BUSReadHalfWrapper;
            return (ulong)ptr;
        }

        public static ulong GetBUSReadWordAddress() {
            delegate* unmanaged[Stdcall]<uint, uint> ptr = &BUSReadWordWrapper;
            return (ulong)ptr;
        }

        public static ulong GetBUSWriteByteAddress() {
            delegate* unmanaged[Stdcall]<uint, byte, void> ptr = &BUSWriteByteWrapper;
            return (ulong)ptr;
        }

        public static ulong GetBUSWriteHalfAddress() {
            delegate* unmanaged[Stdcall]<uint, ushort, void> ptr = &BUSWriteHalfWrapper;
            return (ulong)ptr;
        }

        public static ulong GetBUSWriteWordAddress() {
            delegate* unmanaged[Stdcall]<uint, uint, void> ptr = &BUSWriteWordWrapper;
            return (ulong)ptr;
        }

        public static ulong GetGTEReadAddress() {
            delegate* unmanaged[Stdcall]<uint, uint> ptr = &GTEReadWrapper;
            return (ulong)ptr;
        }

        public static ulong GetGTEWriteAddress() {
            delegate* unmanaged[Stdcall]<uint, uint, void> ptr = &GTEWriteWrapper;
            return (ulong)ptr;
        }

        public static ulong GetGTExecuteAddress() {
            delegate* unmanaged[Stdcall]<uint, void> ptr = &GTEExecuteWrapper;
            return (ulong)ptr;
        }

        public static ulong GetTTYA0Handler() {
            delegate* unmanaged[Stdcall]<void> ptr = &TTYA0Handler;
            return (ulong)ptr;
        }

        public static ulong GetTTYB0Handler() {
            delegate* unmanaged[Stdcall]<void> ptr = &TTYB0Handler;
            return (ulong)ptr;
        }

        public static ulong GetPrintAddress() {
            delegate* unmanaged[Stdcall]<uint, void> ptr = &Print;
            return (ulong)ptr;
        }
    }
}
