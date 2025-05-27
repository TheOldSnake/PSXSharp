using System;
using System.Reflection.Emit;
using System.Reflection;
using PSXSharp.Core.Common;

namespace PSXSharp.Core.MSIL_Recompiler {

    //Implements R3000 MIPS instructions in MSIL
    public static class MSIL_JIT {          
        [assembly: AllowPartiallyTrustedCallers]
        [assembly: SecurityTransparent]
        [assembly: SecurityRules(SecurityRuleSet.Level2, SkipVerificationInFullTrust = true)]

        //Get all needed fields and methods
        private static FieldInfo? GPR = typeof(CPU_MSIL_Recompiler).GetField("GPR");
        private static FieldInfo? CurrentCycle = typeof(CPU_MSIL_Recompiler).GetField("CurrentCycle");

        private static FieldInfo? DirectWrite = typeof(CPU_MSIL_Recompiler).GetField("DirectWrite");
        private static FieldInfo? DelayedWrite = typeof(CPU_MSIL_Recompiler).GetField("DelayedRegisterLoad");     //For memory access
        private static FieldInfo? ReadyWrite = typeof(CPU_MSIL_Recompiler).GetField("ReadyRegisterLoad");        //LWL/LWR Need to access it

        private static FieldInfo? RegisterNumber = typeof(CPU.RegisterLoad).GetField("RegisterNumber");
        private static FieldInfo? Value = typeof(CPU.RegisterLoad).GetField("Value");

        private static FieldInfo? COP0 = typeof(CPU_MSIL_Recompiler).GetField("Cop0");
        private static FieldInfo? COP0_SR = typeof(CPU_MSIL_Recompiler.COP0).GetField("SR");
        private static FieldInfo? COP0_Cause = typeof(CPU_MSIL_Recompiler.COP0).GetField("Cause");
        private static FieldInfo? COP0_EPC = typeof(CPU_MSIL_Recompiler.COP0).GetField("EPC");

        private static FieldInfo? Current_PC = typeof(CPU_MSIL_Recompiler).GetField("Current_PC");
        private static FieldInfo? PC = typeof(CPU_MSIL_Recompiler).GetField("PC");
        private static FieldInfo? Next_PC = typeof(CPU_MSIL_Recompiler).GetField("Next_PC");
  
        private static FieldInfo? BranchBool = typeof(CPU_MSIL_Recompiler).GetField("Branch");
        private static FieldInfo? DelaySlotBool = typeof(CPU_MSIL_Recompiler).GetField("DelaySlot");

        private static FieldInfo? HI = typeof(CPU_MSIL_Recompiler).GetField("HI");
        private static FieldInfo? LO = typeof(CPU_MSIL_Recompiler).GetField("LO");

        private static FieldInfo? BUS = typeof(CPU_MSIL_Recompiler).GetField("BUS");
        private static FieldInfo? GTE = typeof(CPU_MSIL_Recompiler).GetField("GTE");

        private static MethodInfo? BUS_LoadWordFunction = typeof(BUS).GetMethod("LoadWord");
        private static MethodInfo? BUS_LoadHalfFunction = typeof(BUS).GetMethod("LoadHalf");
        private static MethodInfo? BUS_LoadByteFunction = typeof(BUS).GetMethod("LoadByte");
        private static MethodInfo? BUS_StoreWordFunction = typeof(BUS).GetMethod("StoreWord");
        private static MethodInfo? BUS_StoreHalfFunction = typeof(BUS).GetMethod("StoreHalf");
        private static MethodInfo? BUS_StoreByteFunction = typeof(BUS).GetMethod("StoreByte");

        private static MethodInfo? GTE_readFunction = typeof(GTE).GetMethod("read");
        private static MethodInfo? GTE_WriteFunction = typeof(GTE).GetMethod("write");
        private static MethodInfo? GTE_executeFunction = typeof(GTE).GetMethod("execute");

        private static MethodInfo? ExceptionFunction = typeof(CPU_MSIL_Recompiler).GetMethod("Exception");
        private static MethodInfo? BranchFunction = typeof(CPU_MSIL_Recompiler).GetMethod("branch");
        private static MethodInfo? Get_IscIsolateCacheFunction = typeof(CPU_MSIL_Recompiler).GetMethod("get_IscIsolateCache");

        public static void EmitSyscall(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            //Call Exception function with Syscall Value (0x8) 

            il.Emit(OpCodes.Ldarg, 0);                  //Load cpu
            il.Emit(OpCodes.Ldc_I4, 0x8);               //0x8
            il.Emit(OpCodes.Call, ExceptionFunction);   //Call Exception
        }

        public static void EmitBreak(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            //Call Exception function with Break Value (0x9) 
            il.Emit(OpCodes.Ldarg, 0);                  //Load cpu
            il.Emit(OpCodes.Ldc_I4, 0x9);               //0x9
            il.Emit(OpCodes.Call, ExceptionFunction);   //Call Exception
        }

        public static void EmitSlti(uint rs, uint rt, uint imm, bool signed, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            Label done = il.DefineLabel();
            LocalBuilder result = il.DeclareLocal(typeof(int));

            //Set result to 0 initially
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stloc, result.LocalIndex);

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GPR);                                    //Load the GPR array
            il.Emit(OpCodes.Ldc_I4, rs);                                    //Load the first register number

            if (signed) {
                il.Emit(OpCodes.Ldelem_I4);                                    //Load 32-bit int from GPR[rs]
                il.Emit(OpCodes.Ldc_I4, (int)imm);                             //Load 32-bit int imm
                il.Emit(OpCodes.Bge, done);                                    //If the >= we don't set
            } else {
                il.Emit(OpCodes.Ldelem_U4);                                    //Load 32-bit uint from GPR[rs]
                il.Emit(OpCodes.Ldc_I4, imm);                                  //Load 32-bit uint imm
                il.Emit(OpCodes.Bge_Un, done);                                 //If the >= we don't set
            }

            //If we reach here we set the result to 1 
            il.Emit(OpCodes.Ldc_I4, 1);
            il.Emit(OpCodes.Stloc, result.LocalIndex);


            il.MarkLabel(done);
            EmitRegWrite(il, rt, result, false);
        }

        public static void EmitSlt(uint rs, uint rt, uint rd, bool signed, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            Label done = il.DefineLabel();
            LocalBuilder result = il.DeclareLocal(typeof(int));

            //Set result to 0 initially
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stloc, result.LocalIndex);

            if (signed) {
                EmitRegRead(il, rs);
                il.Emit(OpCodes.Conv_I4);
                EmitRegRead(il, rt);
                il.Emit(OpCodes.Conv_I4);

                il.Emit(OpCodes.Bge, done);                                      //If the >= we don't set

            } else {
                EmitRegRead(il, rs);
                EmitRegRead(il, rt);

                il.Emit(OpCodes.Bge_Un, done);                                   //If the >= we don't set
            }

            //If we reach here we set the result to 1 
            il.Emit(OpCodes.Ldc_I4, 1);
            il.Emit(OpCodes.Stloc, result.LocalIndex);

            il.MarkLabel(done);

            EmitRegWrite(il, rd, result, false);
        }


        public static void EmitBranchIf(uint rs, uint rt, uint imm, int type, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder nextPC_Copy = il.DeclareLocal(typeof(uint));
            Label no_branch = il.DefineLabel();

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, Next_PC);                                //Load CPU.Next_PC
            il.Emit(OpCodes.Stloc, nextPC_Copy.LocalIndex);                 //Save Next_PC in a local variable


            EmitRegRead(il, rs);
            il.Emit(OpCodes.Conv_I4);

            if (type > 1) {
                il.Emit(OpCodes.Ldc_I4, 0);                                     //Load 0, ignore the rt parameter 
            } else {
                EmitRegRead(il, rt);
                il.Emit(OpCodes.Conv_I4);
            }

            //The inverse means we don't branch
            switch (type) {
                //0,1 are comparing with GPR[rt] 
                case BranchIf.BEQ: il.Emit(OpCodes.Bne_Un, no_branch); break;
                case BranchIf.BNE: il.Emit(OpCodes.Beq, no_branch); break;

                //2,3 are comparing with 0 
                case BranchIf.BLEZ: il.Emit(OpCodes.Bgt, no_branch); break;
                case BranchIf.BGTZ: il.Emit(OpCodes.Ble, no_branch); break;

                default: throw new Exception("Invalid Type: " + type);
            }

            il.Emit(OpCodes.Ldarg_0);                                       //We branch if the above fails, load cpu
            il.Emit(OpCodes.Ldc_I4, imm);                                   //load offset
            il.Emit(OpCodes.Call, BranchFunction);                          //call branch()

            il.MarkLabel(no_branch);

            //No link for these instructions
        }

        public static void EmitJalr(uint rs, uint rd, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder targetAddress = il.DeclareLocal(typeof(uint));

            //Link to reg rd then jump
            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);                           //Load the address of the Direct Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, Next_PC);                                //Load CPU.Next_PC
            il.Emit(OpCodes.Stfld, Value);                                  //Load the address of the Value field from that Direct Write

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);                           //Load the address of the Direct Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, rd);                                    //Load $rd
            il.Emit(OpCodes.Stfld, RegisterNumber);                         //Set CPU.DirectWrite.RegisterNumber = $rd

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GPR);                                    //Load the GPR array
            il.Emit(OpCodes.Ldc_I4, rs);                                    //Load the first register number
            il.Emit(OpCodes.Ldelem_I4);                                     //Load 32-bit from GPR[rs]
            il.Emit(OpCodes.Stloc, targetAddress.LocalIndex);               //Save the address                    

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldloc, targetAddress);                         //Load the target address of the jump
            il.Emit(OpCodes.Stfld, Next_PC);                                //Set CPU.Next_PC = targetAddress

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldc_I4, 1);                                     //Load 1 which means true
            il.Emit(OpCodes.Stfld, BranchBool);                             //Set CPU.Branch = true
        }

        public static void EmitJR(uint rs, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder targetAddress = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rs);
            il.Emit(OpCodes.Stloc, targetAddress.LocalIndex);               //Save the address                    

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldloc, targetAddress);                          //Load the target address of the jump
            il.Emit(OpCodes.Stfld, Next_PC);                                //Set CPU.Next_PC = targetAddress

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldc_I4, 1);                                     //Load 1 which means true
            il.Emit(OpCodes.Stfld, BranchBool);                             //Set CPU.Branch = true
        }

        public static void EmitJal(uint targetAddress, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            //Link to reg 31 then jump
            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);                           //Load the address of the Direct Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, Next_PC);                                //Load CPU.Next_PC
            il.Emit(OpCodes.Stfld, Value);                                  //Load the address of the Value field from that Direct Write

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);                           //Load the address of the Direct Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, (int)CPU.Register.ra);                  //Load $ra
            il.Emit(OpCodes.Stfld, RegisterNumber);                         //Set CPU.DirectWrite.RegisterNumber = $ra

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldc_I4, targetAddress);                         //Load the target address of the jump
            il.Emit(OpCodes.Stfld, Next_PC);                                //Set CPU.Next_PC = targetAddress

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldc_I4, 1);                                     //Load 1 which means true
            il.Emit(OpCodes.Stfld, BranchBool);                             //Set CPU.Branch = true
        }

        public static void EmitJump(uint targetAddress, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldc_I4, targetAddress);                         //Load the target address of the jump
            il.Emit(OpCodes.Stfld, Next_PC);                                //Set CPU.Next_PC = targetAddress

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldc_I4, 1);                                     //Load 1 which means true
            il.Emit(OpCodes.Stfld, BranchBool);                             //Set CPU.Branch = true
        }

        public static void EmitBXX(uint rs, uint imm, bool link, bool bgez, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder nextPC_Copy = il.DeclareLocal(typeof(uint));
            Label no_branch = il.DefineLabel();

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, Next_PC);                                //Load CPU.Next_PC
            il.Emit(OpCodes.Stloc, nextPC_Copy.LocalIndex);                 //Save Next_PC in a local variable

            EmitRegRead(il, rs);
            il.Emit(OpCodes.Conv_I4);

            il.Emit(OpCodes.Ldc_I4, 0);                                     //Load 0
            il.Emit(bgez ? OpCodes.Blt : OpCodes.Bge, no_branch);           //The inverse means we don't branch
            il.Emit(OpCodes.Ldarg_0);                                       //Else we branch, load cpu
            il.Emit(OpCodes.Ldc_I4, imm);                                   //load offset
            il.Emit(OpCodes.Call, BranchFunction);                          //call branch()


            il.MarkLabel(no_branch);
            if (link) {
                //link to reg 31
                EmitRegWrite(il, 31, nextPC_Copy, false);
            }
        }

        public static void EmitArithmeticU(uint rs, uint rt, uint rd, int type, MSILCacheBlock cache) {
            //We just emit normal add since we don't check for overflows anyway
            EmitArithmetic(rs, rt, rd, type, cache);
        }

        public static void EmitArithmeticI_U(uint rs, uint rt, uint imm, int type, MSILCacheBlock cache) {
            //We just emit normal addi since we don't check for overflows anyway
            EmitArithmetic_i(rs, rt, imm, type, cache);
        }

        public static void EmitArithmetic_i(uint rs, uint rt, uint imm, int type, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            //This should check for signed overflow, but it can be ignored as no games rely on this 

            LocalBuilder res = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rs);
            il.Emit(OpCodes.Conv_I4);

            il.Emit(OpCodes.Ldc_I4, imm);                //Load the immediate 

            //Emit the required op
            switch (type) {
                case ArithmeticSignals.ADD: il.Emit(OpCodes.Add); break;
                case ArithmeticSignals.SUB: il.Emit(OpCodes.Sub); break;
                default: throw new Exception("JIT: Unknown Arithmetic_i : " + type);
            }

            il.Emit(OpCodes.Stloc, res.LocalIndex);      //Store result in local variable
            EmitRegWrite(il, rt, res, false);
        }

        public static void EmitArithmetic(uint rs, uint rt, uint rd, int type, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            //This should check for signed overflow, but it can be ignored as no games rely on this 

            LocalBuilder res = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rs);
            il.Emit(OpCodes.Conv_I4);

            EmitRegRead(il, rt);
            il.Emit(OpCodes.Conv_I4);

            //Emit the required op
            switch (type) {
                case ArithmeticSignals.ADD: il.Emit(OpCodes.Add); break;
                case ArithmeticSignals.SUB: il.Emit(OpCodes.Sub); break;
                default: throw new Exception("JIT: Unknown Arithmetic: " + type);
            }

            il.Emit(OpCodes.Stloc, res.LocalIndex);      //Store result in local variable

            EmitRegWrite(il, rd, res, false);
        }

        public static void EmitLogic_i(uint rs, uint rt, uint imm, int type, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;
            
            LocalBuilder res = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rs);
            il.Emit(OpCodes.Conv_I4);

            il.Emit(OpCodes.Ldc_I4, imm);                //Load the immediate 

            //Emit the required op
            switch (type) {
                case LogicSignals.AND: il.Emit(OpCodes.And); break;
                case LogicSignals.OR: il.Emit(OpCodes.Or); break;
                case LogicSignals.XOR: il.Emit(OpCodes.Xor); break;
                //There is no NORI instruction
                default: throw new Exception("JIT: Unknown Logic_i : " + type);
            }

            il.Emit(OpCodes.Stloc, res.LocalIndex);      //Store result in local variable

            EmitRegWrite(il, rt, res, false);
        }

        public static void EmitLogic(uint rs, uint rt, uint rd, int type, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder res = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rs);

            EmitRegRead(il, rt);    

            //Emit the required op
            switch (type) {
                case LogicSignals.AND: il.Emit(OpCodes.And); break;
                case LogicSignals.OR: il.Emit(OpCodes.Or); break;
                case LogicSignals.XOR: il.Emit(OpCodes.Xor); break;
                case LogicSignals.NOR:
                    il.Emit(OpCodes.Or);
                    il.Emit(OpCodes.Not);
                    break;
                default: throw new Exception("JIT: Unknown Logic: " + type);
            }

            il.Emit(OpCodes.Stloc, res.LocalIndex);      //Store result in local variable

            EmitRegWrite(il, rd, res, false);
        }

        public static void EmitShift(uint rt, uint rd, uint amount, uint direction, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder res = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rt);
            il.Emit(OpCodes.Ldc_I4, amount);             //Load the amount

            //Perform the required shift
            switch (direction) {
                case ShiftSignals.LEFT: il.Emit(OpCodes.Shl); break;
                case ShiftSignals.RIGHT: il.Emit(OpCodes.Shr_Un); break;
                case ShiftSignals.RIGHT_ARITHMETIC: il.Emit(OpCodes.Shr); break;
                default: throw new Exception("Unknown Shift direction");
            }

            il.Emit(OpCodes.Stloc, res.LocalIndex);

            EmitRegWrite(il, rd, res, false);
        }

        public static void EmitShift_v(uint rs, uint rt, uint rd, uint direction, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder res = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rt);

            EmitRegRead(il, rs);

            //The shift amount (rs value) has to be masked with 0x1F
            il.Emit(OpCodes.Ldc_I4, 0x1F);
            il.Emit(OpCodes.And);

            //Perform the required shift
            switch (direction) {
                case ShiftSignals.LEFT: il.Emit(OpCodes.Shl); break;
                case ShiftSignals.RIGHT: il.Emit(OpCodes.Shr_Un); break;
                case ShiftSignals.RIGHT_ARITHMETIC: il.Emit(OpCodes.Shr); break;
                default: throw new Exception("Unknown Shift direction");
            }

            il.Emit(OpCodes.Stloc, res.LocalIndex);

            EmitRegWrite(il, rd, res, false);
        }

        public static void EmitDIV(uint rs, uint rt, bool signed, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder numerator = il.DeclareLocal(typeof(uint));
            LocalBuilder denominator = il.DeclareLocal(typeof(uint));

            LocalBuilder quotient = il.DeclareLocal(typeof(uint));
            LocalBuilder remainder = il.DeclareLocal(typeof(uint));

            Label normalCase = il.DefineLabel();

            Label check2 = il.DefineLabel();
            Label check3 = il.DefineLabel();
            Label End = il.DefineLabel();


            EmitRegRead(il, rs);
            il.Emit(OpCodes.Stloc, numerator.LocalIndex);                        //Save it

            EmitRegRead(il, rt);
            il.Emit(OpCodes.Stloc, denominator.LocalIndex);                       //Save it

            if (signed) {
                //Special cases fo signed div

                //Check 1
                //if numerator >= 0 && denominator == 0:
                il.Emit(OpCodes.Ldloc, numerator.LocalIndex);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ldc_I4, 0);
                il.Emit(OpCodes.Blt, check2);
                il.Emit(OpCodes.Ldloc, denominator.LocalIndex);
                il.Emit(OpCodes.Ldc_I4, 0);
                il.Emit(OpCodes.Bne_Un, check2);

                //if neither branches happen we are in this case
                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldc_I4, 0xffffffff);                              //Load 0xffffffff
                il.Emit(OpCodes.Stfld, LO);                                       //Write it to LO

                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldloc, numerator.LocalIndex);                     //Load numerator
                il.Emit(OpCodes.Stfld, HI);                                       //Write it to HI
                il.Emit(OpCodes.Br, End);                                          


                il.MarkLabel(check2);
                //if numerator < 0 && denominator == 0
                il.Emit(OpCodes.Ldloc, numerator.LocalIndex);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ldc_I4, 0);
                il.Emit(OpCodes.Bge, check3);
                il.Emit(OpCodes.Ldloc, denominator.LocalIndex);
                il.Emit(OpCodes.Ldc_I4, 0);
                il.Emit(OpCodes.Bne_Un, check3);

                //if neither branches happen we are in this case
                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldc_I4, 1);                                       //Load 1
                il.Emit(OpCodes.Stfld, LO);                                       //Write it to LO

                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldloc, numerator.LocalIndex);                     //Load numerator
                il.Emit(OpCodes.Stfld, HI);                                       //Write it to HI
                il.Emit(OpCodes.Br, End);


                il.MarkLabel(check3);
                //if (uint)numerator == 0x80000000 && (uint)denominator == 0xffffffff
                il.Emit(OpCodes.Ldloc, numerator.LocalIndex);
                il.Emit(OpCodes.Ldc_I4, 0x80000000);
                il.Emit(OpCodes.Bne_Un, normalCase);
                il.Emit(OpCodes.Ldloc, denominator.LocalIndex);
                il.Emit(OpCodes.Ldc_I4, 0xffffffff);
                il.Emit(OpCodes.Bne_Un, normalCase);

                //if neither branches happen we are in this case
                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldc_I4, 0x80000000);                              //Load 0x80000000
                il.Emit(OpCodes.Stfld, LO);                                       //Write it to LO

                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldc_I4, 0);                                       //Load 0
                il.Emit(OpCodes.Stfld, HI);                                       //Write it to HI
                il.Emit(OpCodes.Br, End);

            } else {
                //One special case if denominator is 0
                il.Emit(OpCodes.Ldloc, denominator.LocalIndex);
                il.Emit(OpCodes.Ldc_I4, 0);
                il.Emit(OpCodes.Bne_Un, normalCase);
         
                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldc_I4, 0xffffffff);                              //Load 0xffffffff
                il.Emit(OpCodes.Stfld, LO);                                       //Write it to LO

                il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
                il.Emit(OpCodes.Ldloc, numerator.LocalIndex);                     //Load numerator
                il.Emit(OpCodes.Stfld, HI);                                       //Write it to HI
                il.Emit(OpCodes.Br, End);
            }



            il.MarkLabel(normalCase);
            il.Emit(OpCodes.Ldloc, numerator.LocalIndex);                     //Load numerator
            il.Emit(OpCodes.Ldloc, denominator.LocalIndex);                   //Load denominator
            il.Emit(signed? OpCodes.Div : OpCodes.Div_Un);                    //Divide 
            il.Emit(OpCodes.Stloc, quotient.LocalIndex);                      //Save quotient

            il.Emit(OpCodes.Ldloc, numerator.LocalIndex);                     //Load numerator
            il.Emit(OpCodes.Ldloc, denominator.LocalIndex);                   //Load denominator
            il.Emit(signed? OpCodes.Rem : OpCodes.Rem_Un);                    //Mod 
            il.Emit(OpCodes.Stloc, remainder.LocalIndex);                     //Save remainder

            il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
            il.Emit(OpCodes.Ldloc, quotient.LocalIndex);                      //Load the quotient
            il.Emit(OpCodes.Stfld, LO);                                       //Write it to LO

            il.Emit(OpCodes.Ldarg, 0);                                        //Load CPU object reference
            il.Emit(OpCodes.Ldloc, remainder.LocalIndex);                     //Load the remainder
            il.Emit(OpCodes.Stfld, HI);                                       //Write it to HI

            il.MarkLabel(End);
        }

        public static void EmitMULT(uint rs, uint rt, bool signed, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder result = il.DeclareLocal(typeof(ulong));
            
            EmitRegRead(il, rs);
            il.Emit(signed? OpCodes.Conv_I8 : OpCodes.Conv_U8);                  //Extend to 64 bits

            EmitRegRead(il, rt);
            il.Emit(signed ? OpCodes.Conv_I8 : OpCodes.Conv_U8);                  //Extend to 64 bits

            il.Emit(OpCodes.Mul);                                                 //Perform multiplication 
            il.Emit(OpCodes.Conv_U8);                                             //Cast to unsigned 64 bits
            il.Emit(OpCodes.Stloc, result.LocalIndex);                            //Save the value

            il.Emit(OpCodes.Ldarg, 0);                                            //Load CPU object reference
            il.Emit(OpCodes.Ldloc, result.LocalIndex);                            //Load the value
            il.Emit(OpCodes.Conv_U4);                                             //Convert to uint
            il.Emit(OpCodes.Stfld, LO);                                           //Write it to LO

            il.Emit(OpCodes.Ldarg, 0);                                            //Load CPU object reference
            il.Emit(OpCodes.Ldloc, result.LocalIndex);                            //Load the value
            il.Emit(OpCodes.Ldc_I4, 32);                                          //Load 32
            il.Emit(OpCodes.Shr_Un);                                              // >> 32
            il.Emit(OpCodes.Conv_U4);                                             //Convert to uint
            il.Emit(OpCodes.Stfld, HI);                                           //Write it to HI
        }

        public static void EmitLUI(uint rt, uint imm, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            EmitRegWrite(il, rt, imm << 16, false);     
        }

        public static void EmitMTC0(uint rt, uint rd, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder regValue = il.DeclareLocal(typeof(uint));

            if (rd == 12) {
                //cpu.Cop0.SR = cpu.GPR[instruction.Get_rt()]; -> That's what we care about for now
                EmitRegRead(il, rt);                                //Read GPR[rt]
                il.Emit(OpCodes.Stloc, regValue.LocalIndex);        //Save it in a local variable

                il.Emit(OpCodes.Ldarg, 0);                          //Load CPU object reference
                il.Emit(OpCodes.Ldflda, COP0);                      //Load the address of the COP0 field from that cpu object in the stack 
                il.Emit(OpCodes.Ldloc, regValue.LocalIndex);        //Load the value of GPR[rt]
                il.Emit(OpCodes.Stfld, COP0_SR);                    //Set CPU.COP0.SR = GPR[rt]
            }
        }

        public static void EmitMFC0(uint rt, uint rd, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder regValue = il.DeclareLocal(typeof(uint));
            Label end = il.DefineLabel();
            //MFC has load delay!


            //We load different registers depending on rd
            if (rd == 15) {
                //cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();
                il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
                il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Delayed Write field from that cpu object in the stack 
                il.Emit(OpCodes.Ldc_I4, rt);                 //Load rt
                il.Emit(OpCodes.Stfld, RegisterNumber);      //Set CPU.DelayedRegisterLoad.RegisterNumber = rt

                il.Emit(OpCodes.Ldc_I4, 0x00000002);     //Load COP0 R15 (PRID)   
            } else if (rd >= 12 && rd <= 14) {

                //cpu.DelayedRegisterLoad.RegisterNumber = instruction.Get_rt();
                il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
                il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Delayed Write field from that cpu object in the stack 
                il.Emit(OpCodes.Ldc_I4, rt);                 //Load rt
                il.Emit(OpCodes.Stfld, RegisterNumber);      //Set CPU.DelayedRegisterLoad.RegisterNumber = rt

                il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
                il.Emit(OpCodes.Ldflda, COP0);               //Load the address of cop0

                switch (rd) {
                    case 12: il.Emit(OpCodes.Ldfld, COP0_SR); break;        //Load CPU.COP0.SR
                    case 13: il.Emit(OpCodes.Ldfld, COP0_Cause); break;     //Load CPU.COP0.Cause
                    case 14: il.Emit(OpCodes.Ldfld, COP0_EPC); break;       //Load CPU.COP0.EPC
                }

            } else {
                //We return without setting anything
                il.EmitWriteLine("[JIT] Unhandled cop0 Register Read");
                il.Emit(OpCodes.Br, end);
            }

            il.Emit(OpCodes.Stloc, regValue.LocalIndex); //Store the value 

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Delayed Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldloc, regValue.LocalIndex); //Load the value 
            il.Emit(OpCodes.Stfld, Value);               //Set CPU.DelayedWrite.Value = result

            il.MarkLabel(end);
        }

        public static void EmitRFE(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder originalSr = il.DeclareLocal(typeof(uint));
            LocalBuilder srMasked = il.DeclareLocal(typeof(uint));
            LocalBuilder finalRes = il.DeclareLocal(typeof(uint));

            /*
            
            //PSX-SPX: bit2-3 are copied to bit0-1, and bit4-5 are copied to bit2-3, all other bits (including bit4-5) are left unchanged.

            uint temp = cpu.Cop0.SR;
            cpu.Cop0.SR = (uint)(cpu.Cop0.SR & (~0xF));
            cpu.Cop0.SR |= ((temp >> 2) & 0xF);

            */

            il.Emit(OpCodes.Ldarg, 0);                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, COP0);                  //Load address of cop0  
            il.Emit(OpCodes.Ldfld, COP0_SR);                //Load sr from cop0 
            il.Emit(OpCodes.Dup);                           //Duplicate it because we need it after this
            il.Emit(OpCodes.Stloc, originalSr.LocalIndex);  //Save a copy

            //We have the other copy ready
            il.Emit(OpCodes.Ldc_I4, ~0xF);                      //Mask 
            il.Emit(OpCodes.And);                               //Perform AND
            il.Emit(OpCodes.Stloc, srMasked.LocalIndex);        //Save masked sr

            il.Emit(OpCodes.Ldloc, originalSr.LocalIndex);  //Load the copy
            il.Emit(OpCodes.Ldc_I4, 2);                     //Load 2
            il.Emit(OpCodes.Shr_Un);                        //Shift right
            il.Emit(OpCodes.Ldc_I4, 0xF);                   //Load 0xF
            il.Emit(OpCodes.And);                           //AND

            //Now we have the ((temp >> 2) & 0xF) result ready in the stack

            il.Emit(OpCodes.Ldloc, srMasked.LocalIndex);  //Load masked sr

            il.Emit(OpCodes.Or);                         //OR them
            il.Emit(OpCodes.Stloc, finalRes.LocalIndex); //Store the final result

            //Write the final result
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, COP0);               //Load address of cop0  
            il.Emit(OpCodes.Ldloc, finalRes.LocalIndex); //load the final result
            il.Emit(OpCodes.Stfld, COP0_SR);             //Load sr from cop0 
        }


        public static void EmitMF(uint rd, bool isHI, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder regValue = il.DeclareLocal(typeof(uint));

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, isHI ? HI : LO);                         //Load CPU.HI / CPU.LO
            il.Emit(OpCodes.Stloc, regValue.LocalIndex);                    //Save it

            EmitRegWrite(il, rd, regValue, false);        
        }

        public static void EmitMT(uint rs, bool isHI, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder regValue = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rs);
            il.Emit(OpCodes.Stloc, regValue.LocalIndex);                    //Save the value                 

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldloc, regValue.LocalIndex);                    //Load the value
            il.Emit(OpCodes.Stfld, isHI ? HI : LO);                         //Load CPU.HI / CPU.LO
        }

        public static void EmitCOP2Command(uint instruction, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            //Call cpu.GTE.execute(instruction);
            il.Emit(OpCodes.Ldarg, 0);                       //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GTE);                     //Load gte  
            il.Emit(OpCodes.Ldc_I4, instruction);            //Load the parameter
            il.Emit(OpCodes.Callvirt, GTE_executeFunction);  //Call the gte execute
        }

        public static void EmitMFC2_CFC2(uint rt, uint rd, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder result = il.DeclareLocal(typeof(uint));
            
            //Call cpu.GTE.read(rd);
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GTE);                 //Load gte  
            il.Emit(OpCodes.Ldc_I4, rd);                 //Load the parameter
            il.Emit(OpCodes.Callvirt, GTE_readFunction); //Call the gte execute
            il.Emit(OpCodes.Stloc, result.LocalIndex);   //Save the value

            //There is a delay slot
            EmitRegWrite(il, rt, result, true);
        }

        public static void EmitMTC2_CTC2(uint rt, uint rd, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder rtValue = il.DeclareLocal(typeof(uint));

            EmitRegRead(il, rt);                         //Read GPR[rt]
            il.Emit(OpCodes.Stloc, rtValue.LocalIndex);  //Save it

            //Call cpu.GTE.write(rd, value);
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GTE);                 //Load gte  
            il.Emit(OpCodes.Ldc_I4, rd);                 //Load the first parameter
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);  //Load the second parameter
            il.Emit(OpCodes.Callvirt, GTE_WriteFunction); //Call the gte write function
        }

        public static void EmitLWC2(uint rs, uint rt, uint imm, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder address = il.DeclareLocal(typeof(uint));
            LocalBuilder memoryValue = il.DeclareLocal(typeof(uint));

            //Address calculation:
            EmitCalculateAddress(il, rs, imm);
            il.Emit(OpCodes.Stloc, address.LocalIndex);  //Save it

            il.Emit(OpCodes.Ldarg, 0);                       //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                     //Load the bus
            il.Emit(OpCodes.Ldloc, address.LocalIndex);      //Load address
            il.Emit(OpCodes.Callvirt, BUS_LoadWordFunction); //Call bus.loadword          
            il.Emit(OpCodes.Stloc, memoryValue.LocalIndex);  //Save the value

            //Call cpu.GTE.write(rd, value);
            il.Emit(OpCodes.Ldarg, 0);                       //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GTE);                     //Load gte  
            il.Emit(OpCodes.Ldc_I4, rt);                     //Load the first parameter
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);  //Load the second parameter
            il.Emit(OpCodes.Callvirt, GTE_WriteFunction);    //Call the gte write function
        }

        public static void EmitSWC2(uint rs, uint rt, uint imm, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;


            LocalBuilder address = il.DeclareLocal(typeof(uint));
            LocalBuilder gteValue = il.DeclareLocal(typeof(uint));

            //Address calculation:
            EmitCalculateAddress(il, rs, imm);
            il.Emit(OpCodes.Stloc, address.LocalIndex);  //Save it

            //Call cpu.GTE.read(rt);
            il.Emit(OpCodes.Ldarg, 0);                       //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GTE);                     //Load gte  
            il.Emit(OpCodes.Ldc_I4, rt);                     //Load the first parameter
            il.Emit(OpCodes.Callvirt, GTE_readFunction);     //Call the gte read function
            il.Emit(OpCodes.Stloc, gteValue.LocalIndex);     //Save it

            //Write the value to the memory
            il.Emit(OpCodes.Ldarg, 0);                          //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                        //Load the bus object
            il.Emit(OpCodes.Ldloc, address.LocalIndex);         //Load the address
            il.Emit(OpCodes.Ldloc, gteValue.LocalIndex);        //Load the value
            il.Emit(OpCodes.Callvirt, BUS_StoreWordFunction);   //Load the store word function
        }

        public static void EmitMemoryLoad(uint rs, uint rt, uint imm, int size, bool signed, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            Label end = il.DefineLabel();
            
            //Check cache isolation!
            il.Emit(OpCodes.Ldarg, 0);                              //Load CPU object reference
            il.Emit(OpCodes.Callvirt, Get_IscIsolateCacheFunction); //Call "IscIsolateCacheFunction" getter
            il.Emit(OpCodes.Brtrue, end);                           //if true we exit
            

           LocalBuilder address = il.DeclareLocal(typeof(uint));
           LocalBuilder memoryValue = il.DeclareLocal(typeof(uint));


            //Address calculation:
            EmitCalculateAddress(il, rs, imm);
            il.Emit(OpCodes.Stloc, address.LocalIndex);  //Save it

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                 //Load the bus object
            il.Emit(OpCodes.Ldloc, address.LocalIndex);  //Load the address

            switch (size) {
                case MemoryReadWriteSize.BYTE:
                    il.Emit(OpCodes.Callvirt, BUS_LoadByteFunction); //Call LoadByte
                    if (signed) {
                        il.Emit(OpCodes.Conv_I1);   
                    }
                    break;

                case MemoryReadWriteSize.HALF:
                    il.Emit(OpCodes.Callvirt, BUS_LoadHalfFunction); //Call LoadHalf
                    if (signed) {
                        il.Emit(OpCodes.Conv_I2);
                    }
                    break;

                case MemoryReadWriteSize.WORD:
                    il.Emit(OpCodes.Callvirt, BUS_LoadWordFunction); //Call LoadWord, there is no signed/unsigned here since regs are 32 bits
                    break;
            }

            il.Emit(OpCodes.Stloc, memoryValue.LocalIndex);

            //There is a delay slot
            EmitRegWrite(il, rt, memoryValue, true);
          
            il.MarkLabel(end);
        }

        public static void EmitMemoryStore(uint rs, uint rt, uint imm, int size, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            Label end = il.DefineLabel();

            //Check cache isolation!
            il.Emit(OpCodes.Ldarg, 0);                              //Load CPU object reference
            il.Emit(OpCodes.Callvirt, Get_IscIsolateCacheFunction); //Call "IscIsolateCacheFunction" getter
            il.Emit(OpCodes.Brtrue, end);                           //if true we exit


            LocalBuilder address = il.DeclareLocal(typeof(uint));
            LocalBuilder rtValue = il.DeclareLocal(typeof(uint));


            //Address calculation:
            EmitCalculateAddress(il, rs, imm);
            il.Emit(OpCodes.Stloc, address.LocalIndex);  //Save it

            //Load GPR[rt]
            EmitRegRead(il, rt);
            il.Emit(OpCodes.Stloc, rtValue.LocalIndex);  //Save it

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                 //Load the bus object
            il.Emit(OpCodes.Ldloc, address.LocalIndex);  //Load address
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);  //Load value


            switch (size) {
                case MemoryReadWriteSize.BYTE:
                    il.Emit(OpCodes.Conv_U1);                         //cast to unsigned 8 bits
                    il.Emit(OpCodes.Callvirt, BUS_StoreByteFunction); //Call Store Byte       
                    break;

                case MemoryReadWriteSize.HALF:
                    il.Emit(OpCodes.Conv_U2);                         //cast to unsigned 16 bits
                    il.Emit(OpCodes.Callvirt, BUS_StoreHalfFunction); //Call Store Half 
                    break;

                case MemoryReadWriteSize.WORD:
                    il.Emit(OpCodes.Callvirt, BUS_StoreWordFunction); //Call Store Word 
                    break;
            }

            il.MarkLabel(end);
        }

        public static void EmitLWL_LWR(uint rs, uint rt, uint imm, bool left, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;


            LocalBuilder address = il.DeclareLocal(typeof(uint));
            LocalBuilder rtValue = il.DeclareLocal(typeof(uint));
            //LocalBuilder pos = il.DeclareLocal(typeof(uint));
            LocalBuilder memoryValue = il.DeclareLocal(typeof(uint));
            LocalBuilder finalValue = il.DeclareLocal(typeof(uint));

            Label end = il.DefineLabel();
            Label final = il.DefineLabel();
            Label bypassDelay = il.DefineLabel();
            Label loadMemory = il.DefineLabel();

            Label[] caseTable = [il.DefineLabel(), il.DefineLabel(), il.DefineLabel(), il.DefineLabel()];
            Label defaultCase = il.DefineLabel();

            //Check cache isolation!
            il.Emit(OpCodes.Ldarg, 0);                              //Load CPU object reference
            il.Emit(OpCodes.Callvirt, Get_IscIsolateCacheFunction); //Call "IscIsolateCacheFunction" getter
            il.Emit(OpCodes.Brtrue, end);                           //if true we exit


            //Address calculation:
            EmitCalculateAddress(il, rs, imm);
            il.Emit(OpCodes.Stloc, address.LocalIndex);  //Save it

            //We need to check if rt has a delayd load, and we bypass that
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);      //Read the cpu.ReadyRegisterLoad.RegisterNumber
            il.Emit(OpCodes.Ldc_I4, rt);                 //Load rt

            il.Emit(OpCodes.Beq, bypassDelay);           //If they are equal we bypass the delay

            //Otherwise we read from GPR[rt]
            EmitRegRead(il, rt);
            il.Emit(OpCodes.Stloc, rtValue.LocalIndex);  //Save it
            il.Emit(OpCodes.Br, loadMemory);             


            il.MarkLabel(bypassDelay);
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);               //Read the cpu.ReadyRegisterLoad.Value
            il.Emit(OpCodes.Stloc, rtValue.LocalIndex);  //Save it
            //il.Emit(OpCodes.Br, loadMemory);           //No need to branch 


            il.MarkLabel(loadMemory);
            il.Emit(OpCodes.Ldarg, 0);                       //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                     //Load the bus object
            il.Emit(OpCodes.Ldloc, address.LocalIndex);      //Load address
            il.Emit(OpCodes.Ldc_I4, ~3);                     //Load ~3
            il.Emit(OpCodes.And);                            //address &= (~3)
            il.Emit(OpCodes.Callvirt, BUS_LoadWordFunction); //Load from memory            
            il.Emit(OpCodes.Stloc, memoryValue.LocalIndex);  //Save it


            il.Emit(OpCodes.Ldloc, address.LocalIndex);      //Load address
            il.Emit(OpCodes.Ldc_I4, 3);                       //Load 3
            il.Emit(OpCodes.And);                            //address &= (3)

            il.Emit(OpCodes.Switch, caseTable);
            il.Emit(OpCodes.Br, defaultCase);


            il.MarkLabel(caseTable[0]);
            //case 0: finalValue = (current_value & 0x00ffffff) | (word << 24); break;    or     case 0: finalValue = (current_value & 0x00000000) | (word >> 0); break;
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left? 0x00ffffff : 0x00000000);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 24 : 0);
            il.Emit(left? OpCodes.Shl : OpCodes.Shr_Un);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(caseTable[1]);
            //case 1: finalValue = (current_value & 0x0000ffff) | (word << 16); break;   or     case 1: finalValue = (current_value & 0xff000000) | (word >> 8); break;
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0x0000ffff : 0xff000000);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 16 : 8);
            il.Emit(left ? OpCodes.Shl : OpCodes.Shr_Un);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(caseTable[2]);
            //case 2: finalValue = (current_value & 0x000000ff) | (word << 8); break;  or     case 2: finalValue = (current_value & 0xffff0000) | (word >> 16); break;
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0x000000ff : 0xffff0000);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 8 : 16);
            il.Emit(left ? OpCodes.Shl : OpCodes.Shr_Un);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(caseTable[3]);
            //case 3: finalValue = (current_value & 0x00000000) | (word << 0); break;  or     case 3: finalValue = (current_value & 0xffffff00) | (word >> 24); break;
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0x00000000 : 0xffffff00);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0 : 24);
            il.Emit(left ? OpCodes.Shl : OpCodes.Shr_Un);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(defaultCase);
            il.EmitWriteLine("[JIT] lwl/lwr unknown pos");
            il.Emit(OpCodes.Br, end);


            il.MarkLabel(final);
            il.Emit(OpCodes.Stloc, finalValue.LocalIndex);

            //There is a delay slot
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Delayed Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, rt);                 //Load rt
            il.Emit(OpCodes.Stfld, RegisterNumber);      //Set CPU.DelayedRegisterLoad.RegisterNumber = rt

            il.Emit(OpCodes.Ldarg, 0);                         //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);             //Load the address of the Delayed Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldloc, finalValue.LocalIndex);     //Load the value
            il.Emit(OpCodes.Stfld, Value);                     //Set CPU.DelayedRegisterLoad.Value = finalValue

            il.MarkLabel(end);
        }

        public static void EmitSWL_SWR(uint rs, uint rt, uint imm, bool left, MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            LocalBuilder address = il.DeclareLocal(typeof(uint));
            LocalBuilder rtValue = il.DeclareLocal(typeof(uint));
            //LocalBuilder pos = il.DeclareLocal(typeof(uint));
            LocalBuilder memoryValue = il.DeclareLocal(typeof(uint));
            LocalBuilder finalValue = il.DeclareLocal(typeof(uint));

            Label end = il.DefineLabel();
            Label final = il.DefineLabel();
            Label loadMemory = il.DefineLabel();

            Label[] caseTable = [il.DefineLabel(), il.DefineLabel(), il.DefineLabel(), il.DefineLabel()];
            Label defaultCase = il.DefineLabel();

            //Check cache isolation!
            il.Emit(OpCodes.Ldarg, 0);                              //Load CPU object reference
            il.Emit(OpCodes.Callvirt, Get_IscIsolateCacheFunction); //Call "IscIsolateCacheFunction" getter
            il.Emit(OpCodes.Brtrue, end);                           //if true we exit


            //Address calculation:
            EmitCalculateAddress(il, rs, imm);
            il.Emit(OpCodes.Stloc, address.LocalIndex);  //Save it


            //We read from GPR[rt], unlike lwl,lwr, there is no bypassing
            EmitRegRead(il, rt);
            il.Emit(OpCodes.Stloc, rtValue.LocalIndex);  //Save it

            //il.Emit(OpCodes.Br, loadMemory);
        
            il.MarkLabel(loadMemory);
            il.Emit(OpCodes.Ldarg, 0);                       //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                     //Load the bus object
            il.Emit(OpCodes.Ldloc, address.LocalIndex);      //Load address
            il.Emit(OpCodes.Ldc_I4, ~3);                     //Load ~3
            il.Emit(OpCodes.And);                            //address &= (~3)
            il.Emit(OpCodes.Callvirt, BUS_LoadWordFunction); //Load from memory            
            il.Emit(OpCodes.Stloc, memoryValue.LocalIndex);  //Save it


            il.Emit(OpCodes.Ldloc, address.LocalIndex);      //Load address
            il.Emit(OpCodes.Ldc_I4, 3);                       //Load 3
            il.Emit(OpCodes.And);                             //address &= (3)

            il.Emit(OpCodes.Switch, caseTable);
            il.Emit(OpCodes.Br, defaultCase);


            il.MarkLabel(caseTable[0]);
            //case 0: finalValue = (current_value & 0xffffff00) | (value >> 24); break; or case 0: finalValue = (current_value & 0x00000000) | (value << 0); break;
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0xffffff00 : 0x00000000);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 24 : 0);
            il.Emit(left ? OpCodes.Shr_Un : OpCodes.Shl);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(caseTable[1]);
            //case 1: finalValue = (current_value & 0xffff0000) | (value >> 16); break; or  case 1: finalValue = (current_value & 0x000000ff) | (value << 8); break;
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0xffff0000 : 0x000000ff);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 16 : 8);
            il.Emit(left ? OpCodes.Shr_Un : OpCodes.Shl);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(caseTable[2]);
            //case 2: finalValue = (current_value & 0xff000000) | (value >> 8); break; or case 2: finalValue = (current_value & 0x0000ffff) | (value << 16); break;
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0xff000000 : 0x0000ffff);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 8 : 16);
            il.Emit(left ? OpCodes.Shr_Un : OpCodes.Shl);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(caseTable[3]);
            //case 3: finalValue = (current_value & 0x00000000) | (value >> 0); break;  or  case 3: finalValue = (current_value & 0x00ffffff) | (value << 24); break;
            il.Emit(OpCodes.Ldloc, memoryValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0x00000000 : 0x00ffffff);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, rtValue.LocalIndex);
            il.Emit(OpCodes.Ldc_I4, left ? 0 : 24);
            il.Emit(left ? OpCodes.Shr_Un : OpCodes.Shl);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Br, final);

            il.MarkLabel(defaultCase);
            il.EmitWriteLine("[JIT] swl/swr unknown pos");
            il.Emit(OpCodes.Br, end);

            il.MarkLabel(final);
            il.Emit(OpCodes.Stloc, finalValue.LocalIndex);
    
            il.Emit(OpCodes.Ldarg, 0);                          //Load CPU object reference
            il.Emit(OpCodes.Ldfld, BUS);                        //Load the bus object
            il.Emit(OpCodes.Ldloc, address.LocalIndex);         //Load address
            il.Emit(OpCodes.Ldc_I4, ~3);                        //Load ~3
            il.Emit(OpCodes.And);                               //address &= (~3)
            il.Emit(OpCodes.Ldloc, finalValue.LocalIndex);      //Load value
            il.Emit(OpCodes.Callvirt, BUS_StoreWordFunction);   //Call bus store word

            il.MarkLabel(end);
        }

        private static void EmitRegWrite(ILGenerator il, uint index, LocalBuilder writeValue, bool delayed) {
            FieldInfo field;
            if (delayed) {
                field = DelayedWrite;
            } else {
                field = DirectWrite;
            }

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, field);                                 //Load the address of the field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, index);                                 //Load index
            il.Emit(OpCodes.Stfld, RegisterNumber);                         //Set CPU.XX.RegisterNumber = index

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, field);                                 //Load the address of the field from that cpu object in the stack 
            il.Emit(OpCodes.Ldloc, writeValue.LocalIndex);                  //Load value
            il.Emit(OpCodes.Stfld, Value);                                  //Set CPU.XX.Value = writeValue
        }

        //Overload for immediate write
        private static void EmitRegWrite(ILGenerator il, uint index, uint immValue, bool delayed) { 
            FieldInfo field;
            if (delayed) {
                field = DelayedWrite;
            } else {
                field = DirectWrite;
            }

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, field);                                 //Load the address of the field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, index);                                 //Load index
            il.Emit(OpCodes.Stfld, RegisterNumber);                         //Set CPU.XX.RegisterNumber = index

            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldflda, field);                                 //Load the address of the field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, immValue);                              //Load immediate value
            il.Emit(OpCodes.Stfld, Value);                                  //Set CPU.XX.Value = writeValue
        }

        private static void EmitRegRead(ILGenerator il, uint index) {
            il.Emit(OpCodes.Ldarg, 0);                                      //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GPR);                                    //Load the GPR array
            il.Emit(OpCodes.Ldc_I4, index);                                 //Load the register number
            il.Emit(OpCodes.Ldelem_U4);                                     //Load 32-bit uint from GPR[index]
        }


        private static void EmitCalculateAddress(ILGenerator il, uint reg, uint imm) {
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldfld, GPR);                 //Load the GPR array
            il.Emit(OpCodes.Ldc_I4, reg);                //Load reg
            il.Emit(OpCodes.Ldelem_I4);                  //Load 32-bit from GPR[reg]
            il.Emit(OpCodes.Ldc_I4, imm);                //Load imm
            il.Emit(OpCodes.Add);                        //Add them 
        }

        public static void EmitRegisterTransfare(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            Label skip = il.DefineLabel();

            LocalBuilder regNumber = il.DeclareLocal(typeof(uint));
            LocalBuilder regValue = il.DeclareLocal(typeof(uint));

            //////////////////////////////////////////////////////////////////////////
            
             /*if (cpu.ReadyRegisterLoad.RegisterNumber != cpu.DelayedRegisterLoad.RegisterNumber) {
                cpu.GPR[cpu.ReadyRegisterLoad.RegisterNumber] = cpu.ReadyRegisterLoad.Value;
            }*/
             

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Delayed Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);     

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);      

            il.Emit(OpCodes.Beq, skip);                  //if equal skip this 

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, GPR);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);

            il.Emit(OpCodes.Stelem_I4);

            //////////////////////////////////////////////////////////////////////////
            
            il.MarkLabel(skip);                 

            /*
                cpu.ReadyRegisterLoad.Value = cpu.DelayedRegisterLoad.Value;
                cpu.ReadyRegisterLoad.RegisterNumber = cpu.DelayedRegisterLoad.RegisterNumber;    
            */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);
            il.Emit(OpCodes.Stfld, Value);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);
            il.Emit(OpCodes.Stfld, RegisterNumber);

            //////////////////////////////////////////////////////////////////////////

            /*
             cpu.DelayedRegisterLoad.Value = 0;
             cpu.DelayedRegisterLoad.RegisterNumber = 0;
             */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, Value);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, RegisterNumber);

            //////////////////////////////////////////////////////////////////////////
            /*
              //Last step is direct register write, so it can overwrite any memory load on the same register
                cpu.GPR[cpu.DirectWrite.RegisterNumber] = cpu.DirectWrite.Value;
                cpu.DirectWrite.RegisterNumber = 0;
                cpu.DirectWrite.Value = 0;
            */

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, GPR);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);

            il.Emit(OpCodes.Stelem_I4);

            //////////////////////////////////////////////////////////////////////////

            /*
                cpu.DirectWrite.RegisterNumber = 0;
                cpu.DirectWrite.Value = 0;
                cpu.GPR[0] = 0;
             */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, Value);

            //////////////////////////////////////////////////////////////////////////

            /*
               cpu.GPR[0] = 0;
            */

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, GPR);
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stelem_I4);

            //il.Emit(OpCodes.Ldarg, 0);
            //il.Emit(OpCodes.Call, RegisterTransfareMeth);
        }

        public static void EmitBranchDelayHandler(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;

            /*
            cpu.DelaySlot = cpu.Branch;   //Branch delay 
            cpu.Branch = false;
            cpu.PC = cpu.Next_PC;
            cpu.Next_PC = cpu.Next_PC + 4;
             */

            //cpu.DelaySlot = cpu.Branch;   //Branch delay 
            il.Emit(OpCodes.Ldarg, 0);  
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, BranchBool);
            il.Emit(OpCodes.Stfld, DelaySlotBool);

            //cpu.Branch = false;
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, BranchBool);

            //cpu.PC = cpu.Next_PC;
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, Next_PC);
            il.Emit(OpCodes.Stfld, PC);

            //cpu.Next_PC = cpu.Next_PC + 4;
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, Next_PC);
            il.Emit(OpCodes.Ldc_I4, 4);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stfld, Next_PC);

            //il.Emit(OpCodes.Ldarg, 0);
            //il.Emit(OpCodes.Call, BranchDelayMeth);
        }

        public static void EmitSavePC(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;
            /*
             cpu.Current_PC = cpu.PC;
            */

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, PC);
            il.Emit(OpCodes.Stfld, Current_PC);

           // il.Emit(OpCodes.Ldarg, 0);
           // il.Emit(OpCodes.Call, SavePCMeth);
        }

        public static void UpdateCurrentCycle(MSILCacheBlock cache, int cyclesPerInstruction) {
            ILGenerator il = cache.IL_Emitter;
            LocalBuilder result = il.DeclareLocal(typeof(ulong));

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, CurrentCycle);
            il.Emit(OpCodes.Ldc_I4, cyclesPerInstruction);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, result.LocalIndex);

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldloc, result.LocalIndex);
            il.Emit(OpCodes.Stfld, CurrentCycle);
        }

        public static void EmitRet(MSILCacheBlock cache) {
            ILGenerator il = cache.IL_Emitter;
            il.Emit(OpCodes.Ret);
        }

        public static void EmitWriteLineInt(ILGenerator il) {           //Prints the top int in the stack
            MethodInfo writeLineInt = typeof(Console).GetMethod("WriteLine", [typeof(int)]);
            il.Emit(OpCodes.Call, writeLineInt);
        }

        public static DynamicMethod SavePCMeth;
        public static DynamicMethod BranchDelayMeth;
        public static DynamicMethod RegisterTransfareMeth;

        public static void PreCompileSavePC() {
            DynamicMethod method = new DynamicMethod("SavePC", typeof(void), [typeof(CPU_MSIL_Recompiler)], true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, PC);
            il.Emit(OpCodes.Stfld, Current_PC);

            il.Emit(OpCodes.Ret);
            SavePCMeth = method;
        }

        public static void PreCompileDelayHandler() {
            DynamicMethod method = new DynamicMethod("DelayHandler", typeof(void), [typeof(CPU_MSIL_Recompiler)], true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, BranchBool);
            il.Emit(OpCodes.Stfld, DelaySlotBool);

            //cpu.Branch = false;
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, BranchBool);

            //cpu.PC = cpu.Next_PC;
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, Next_PC);
            il.Emit(OpCodes.Stfld, PC);

            //cpu.Next_PC = cpu.Next_PC + 4;
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, Next_PC);
            il.Emit(OpCodes.Ldc_I4, 4);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stfld, Next_PC);

            il.Emit(OpCodes.Ret);
            BranchDelayMeth = method;
        }

        public static void PreCompileRT() {
            DynamicMethod method = new DynamicMethod("RT", typeof(void), [typeof(CPU_MSIL_Recompiler)], true);
            ILGenerator il = method.GetILGenerator();
            Label skip = il.DefineLabel();

            LocalBuilder regNumber = il.DeclareLocal(typeof(uint));
            LocalBuilder regValue = il.DeclareLocal(typeof(uint));

            //////////////////////////////////////////////////////////////////////////
            /*
             if (cpu.ReadyRegisterLoad.RegisterNumber != cpu.DelayedRegisterLoad.RegisterNumber) {
                cpu.GPR[cpu.ReadyRegisterLoad.RegisterNumber] = cpu.ReadyRegisterLoad.Value;
            }
             */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Delayed Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);

            il.Emit(OpCodes.Beq, skip);                  //if equal skip this 

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, GPR);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);

            il.Emit(OpCodes.Stelem_I4);

            //////////////////////////////////////////////////////////////////////////

            il.MarkLabel(skip);

            /*
                cpu.ReadyRegisterLoad.Value = cpu.DelayedRegisterLoad.Value;
                cpu.ReadyRegisterLoad.RegisterNumber = cpu.DelayedRegisterLoad.RegisterNumber;    
            */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);
            il.Emit(OpCodes.Stfld, Value);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, ReadyWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);
            il.Emit(OpCodes.Stfld, RegisterNumber);

            //////////////////////////////////////////////////////////////////////////

            /*
             cpu.DelayedRegisterLoad.Value = 0;
             cpu.DelayedRegisterLoad.RegisterNumber = 0;
             */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, Value);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DelayedWrite);       //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, RegisterNumber);

            //////////////////////////////////////////////////////////////////////////
            /*
              //Last step is direct register write, so it can overwrite any memory load on the same register
                cpu.GPR[cpu.DirectWrite.RegisterNumber] = cpu.DirectWrite.Value;
                cpu.DirectWrite.RegisterNumber = 0;
                cpu.DirectWrite.Value = 0;
            */

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, GPR);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);         //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldfld, Value);

            il.Emit(OpCodes.Stelem_I4);

            //////////////////////////////////////////////////////////////////////////

            /*
                cpu.DirectWrite.RegisterNumber = 0;
                cpu.DirectWrite.Value = 0;
                cpu.GPR[0] = 0;
             */

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, RegisterNumber);

            il.Emit(OpCodes.Ldarg, 0);                   //Load CPU object reference
            il.Emit(OpCodes.Ldflda, DirectWrite);        //Load the address of the Ready Write field from that cpu object in the stack 
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stfld, Value);

            //////////////////////////////////////////////////////////////////////////

            /*
               cpu.GPR[0] = 0;
            */

            il.Emit(OpCodes.Ldarg, 0);
            il.Emit(OpCodes.Ldfld, GPR);
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Ldc_I4, 0);
            il.Emit(OpCodes.Stelem_I4);

            il.Emit(OpCodes.Ret);
            RegisterTransfareMeth = method;
        }
    }

    public class MSILCacheBlock {
        [assembly: AllowPartiallyTrustedCallers]
        [assembly: SecurityTransparent]
        [assembly: SecurityRules(SecurityRuleSet.Level2, SkipVerificationInFullTrust = true)]

        public uint Address;        
        public uint Total;
        public uint Cycles;
        public uint Checksum;
        public bool IsCompiled;

        public DynamicMethod FunctionBlock;
        public ILGenerator IL_Emitter;

        public delegate void Instruction(CPU_MSIL_Recompiler cpu);
        public Instruction FunctionPointer;

        public void Init(uint address) {
            Address = address;
            FunctionBlock = new DynamicMethod("Block", typeof(void),
            [typeof(CPU_MSIL_Recompiler)], true);
            IL_Emitter = FunctionBlock.GetILGenerator(15 * 1024);
            FunctionPointer = null;
            IsCompiled = false;
            Total = 0;
            Checksum = 0;
        }

        public void Compile() {
            FunctionPointer = (Instruction)FunctionBlock.CreateDelegate(typeof(Instruction));
            IsCompiled = true;
            /*if (IL_Emitter.ILOffset >= 15 * 1024) {
                Console.WriteLine("[JIT] Warning Buffer Resize: " + IL_Emitter.ILOffset);
                Console.WriteLine("[JIT] Total: " + Total);
            }*/
        }
    }
}
