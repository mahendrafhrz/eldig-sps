// medevac_fsm.v
// FSM & logic untuk Autonomous Drone MedEvac Coordination (environment controller)
// Berdasarkan persamaan di laporan:
// W = ST + HS
// C = OC + CS + IM + WS
// D1 = C
// D0 = C(ACK + S1S0) + C'W
// HP = C' . ST
// HV = C' . HS
// OM = C
// FS = C
// AT = C
// AL = C . ACK'

module medevac_fsm (
    input  wire clk,
    input  wire rst,        // async reset aktif tinggi -> state NORMAL (00)

    // SENSOR INPUT (digital)
    input  wire ST,         // Skin Temperature flag
    input  wire HS,         // Humidity Sensor flag
    input  wire OC,         // O2 concentration flag
    input  wire CS,         // CO2 sensor flag
    input  wire IM,         // Infant movement flag
    input  wire WS,         // Weight sensor flag

    // CONTROL INPUT
    input  wire ACK,        // Acknowledge dari operator

    // OUTPUT ACTUATOR
    output wire HP,         // Heater Power
    output wire HV,         // Humidifier Valve
    output wire OM,         // O2 Mixer
    output wire FS,         // Fan Speed
    output wire AT,         // Auto Tilt
    output wire AL,         // Alarm

    // DEBUG / MONITOR STATE
    output wire [1:0] state // {S1,S0}: 00=NORMAL,01=WARNING,10=CRITICAL,11=ACKED
);

    // ----------------------------------------------------------------
    // 1. STATE REGISTER (2-bit D Flip-Flop)
    // ----------------------------------------------------------------
    reg [1:0] curr_state;    // S1 = curr_state[1], S0 = curr_state[0]
    assign state = curr_state;

    // ----------------------------------------------------------------
    // 2. FLAG LOGIC: W dan C
    // ----------------------------------------------------------------
    wire W;                  // Warning flag
    wire C;                  // Critical flag

    assign W = ST | HS;                 // W = ST + HS
    assign C = OC | CS | IM | WS;       // C = OC + CS + IM + WS

    // ----------------------------------------------------------------
    // 3. D FLIP-FLOP EXCITATION: D1 dan D0
    //     D1 = C
    //     D0 = C(ACK + S1S0) + C'W
    // ----------------------------------------------------------------
    wire S1, S0;
    assign S1 = curr_state[1];
    assign S0 = curr_state[0];

    wire D1, D0;

    assign D1 = C;

    assign D0 = ( C & ( ACK | (S1 & S0) ) )     // C(ACK + S1S0)
               | ( (~C) & W );                  // + C'W

    // ----------------------------------------------------------------
    // 4. STATE UPDATE (D-FF, edge-triggered, async reset)
    // ----------------------------------------------------------------
    always @(posedge clk or posedge rst) begin
        if (rst) begin
            // NORMAL state = 2'b00
            curr_state <= 2'b00;
        end else begin
            curr_state <= {D1, D0};
        end
    end

    // ----------------------------------------------------------------
    // 5. OUTPUT LOGIC (ACTUATOR)
    //     HP = C' . ST
    //     HV = C' . HS
    //     OM = C
    //     FS = C
    //     AT = C
    //     AL = C . ACK'
    // ----------------------------------------------------------------
    assign HP = (~C) & ST;
    assign HV = (~C) & HS;
    assign OM = C;
    assign FS = C;
    assign AT = C;
    assign AL = C & (~ACK);

endmodule
