// medevac_fsm_tb.v
// Testbench untuk medevac_fsm - Versi LOOP (Pilihan C)
// FSM akan berulang: NORMAL → WARNING → CRITICAL → ACKED → NORMAL → ...

`timescale 1ns/1ps

module medevac_fsm_tb;

    reg clk;
    reg rst;

    reg ST, HS, OC, CS, IM, WS;
    reg ACK;

    wire HP, HV, OM, FS, AT, AL;
    wire [1:0] state;

    // Instansiasi DUT (Device Under Test)
    medevac_fsm uut (
        .clk(clk),
        .rst(rst),
        .ST(ST),
        .HS(HS),
        .OC(OC),
        .CS(CS),
        .IM(IM),
        .WS(WS),
        .ACK(ACK),
        .HP(HP),
        .HV(HV),
        .OM(OM),
        .FS(FS),
        .AT(AT),
        .AL(AL),
        .state(state)
    );

    // ------------------------------------------
    // Clock generator: 10 ns period (100 MHz)
    // ------------------------------------------
    initial begin
        clk = 0;
        forever #5 clk = ~clk;
    end

    // ------------------------------------------
    // Stimulus utama (LOOP FOREVER)
    // ------------------------------------------
    initial begin
        // Dump file untuk GTKWave
        $dumpfile("medevac_fsm.vcd");
        $dumpvars(0, medevac_fsm_tb);

        // -----------------------------
        // INIT
        // -----------------------------
        rst = 1;
        {ST, HS, OC, CS, IM, WS} = 6'b000000;
        ACK = 0;
        #40;

        rst = 0;
        #20;

        // ---------------------------------
        // LOOP SELAMANYA
        // ---------------------------------
        forever begin

            // -------------------------------------------------
            // 1 NORMAL MODE (C=0, W=0)
            // -------------------------------------------------
            {ST, HS, OC, CS, IM, WS} = 6'b000000;
            ACK = 0;
            #100;

            // -------------------------------------------------
            // 2 WARNING MODE (W=1, C=0)
            //    ST = 1 → W = 1 + 0 = 1
            // -------------------------------------------------
            ST = 1; HS = 0; 
            OC = 0; CS = 0; IM = 0; WS = 0;
            ACK = 0;
            #100;

            // -------------------------------------------------
            // 3 CRITICAL MODE (C=1)
            //    OC = 1 → C = 1
            // -------------------------------------------------
            ST = 0; HS = 0;
            OC = 1; CS = 0; IM = 0; WS = 0;
            ACK = 0;
            #100;

            // -------------------------------------------------
            // 4 ACKED MODE (C=1, ACK=1)
            // -------------------------------------------------
            ACK = 1;
            #100;

            // -------------------------------------------------
            // 5 CRITICAL VARIASI (masih C=1 tapi sensor lain)
            // -------------------------------------------------
            OC = 0; CS = 1; IM = 1; WS = 0;
            ST = 0; HS = 0;
            ACK = 1;
            #100;

            // -------------------------------------------------
            // 6 Kembali NORMAL (C=0, W=0)
            // -------------------------------------------------
            OC = 0; CS = 0; IM = 0; WS = 0;
            ST = 0; HS = 0;
            ACK = 0;
            #100;

            // Setelah ini loop kembali ke NORMAL lagi
        end
    end

endmodule
