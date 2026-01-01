# Nissan Leaf EV-CAN Signal Glossary (from EV-can_AZE0.dbc)

### 0x1D4 — x1D4 (DLC 8, TX VCM, 10ms)
*Message notes:* Vehicle Control Module (10ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| TargetMotorTorque |  | 23 | 12 | BE | Y | 0.25 | 0.0 | 0.0..1024.0 | Nm | Requested Torque sent to inverter |
| HCM_CLOCK |  | 38 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | - | PRUN Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| StatusOfHighVoltagePowerSupply |  | 34 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | - | 0=High Voltage not supplied, 1=High Voltage supplied; BTONFN |
| Relay_Plus_Output_Status |  | 46 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | - | 0=Main Relay Plus not output, 1=Main Relay Plus ON; RLYP |
| CRC_1D4 |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 |  |  |
| ChargeStatus |  | 52 | 5 | BE | N | 1.0 | 0.0 | 0.0..255.0 | MODEMASK | 140=Charging interrupted, 224=Charging; During charging, the first 3 MSBs are high, and if user aborts charge, the highest MSB will go low. Useful for detecting that charge was interrupted by user unplugging the charge cable. |
| Motor_vibratin_const_stat |  | 45 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| Inhibitor_Pos |  | 42 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  | 0=NA, 1=R, 2=D, 3=P |
| Gear_shift_ingibitor_req |  | 55 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| MotorTqLimitUpper |  | 7 | 8 | BE | N | 2.5 | 0.0 | 0.0..0.0 | Nm |  |
| MotorTqLimitLower |  | 15 | 8 | BE | N | -2.5 | 0.0 | 0.0..0.0 | Nm |  |
| BrakePedalPressed |  | 53 | 1 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |

### 0x1DA — x1DA (DLC 8, TX INVmc, 10ms)
*Message notes:* Inverter (10ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| MG_InputVoltage |  | 0 | 8 | LE | N | 2.0 | 0.0 | 0.0..508.0 | V |  |
| MG_EffectiveTorque |  | 18 | 11 | BE | Y | 0.5 | 0.0 | -274.0..274.0 | Nm | STMG -  Response from Inverter how much torque was applied (Demand is in 0x1D4) Note that value is 2S! |
| MG_OutputRevolution |  | 39 | 15 | BE | Y | 1.0 | 0.0 | -16382.0..16382.0 | rpm |  |
| MG_CLOCK |  | 48 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | PRUN | Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| CRC_1DA |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | CRC | CRC |
| MG_ErrorCodes |  | 50 | 6 | LE | N | 1.0 | 0.0 | 0.0..0.0 | status |  |

### 0x1DB — x1DB (DLC 8, TX HVBAT, 10ms)
*Message notes:* Lithium Battery Controller (10ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LB_Current |  | 7 | 11 | BE | Y | 0.5 | 0.0 | -400.0..200.0 | A | BatteryCurrentSignal , 2s comp, 1lSB = 0.5A |
| LB_Relay_Cut_Request |  | 11 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=No-Request, 1=Main Relay OFF Request, 2=Main Relay OFF Request, 3=Main Relay OFF Request; 00 = No-Request 01 = Main Relay OFF Request 10 = Main Relay OFF Request 11 = Main Relay OFF Request |
| LB_Failsafe_Status |  | 8 | 3 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=Normal Start Request, 1=Normal Stop Request, 2=Charging Mode Stop Request, 3=Charging Mode Stop Request & Normal Stop Request, 4=Caution Lamp Request, 5=Caution Lamp Request & Normal Stop Request, 6=Caution Lamp Request & Charging Mode Stop Request, 7=Caution Lamp Request & Charging Mode Stop Request & Normal Stop Request |
| LB_Total_Voltage |  | 23 | 10 | BE | N | 0.5 | 0.0 | 0.0..450.0 | V |  |
| LB_MainRelayOn_flag |  | 29 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=No-Permission, 1=Main Relay On Permission; 0h = No-Permission 1h = Main Relay On Permission |
| LB_Full_CHARGE_flag |  | 28 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| LB_INTER_LOCK |  | 27 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=Not Inter Lock connected, 1=Inter Lock connected; 0h = Not Inter Lock connected 1h = Inter Lock connected |
| LB_Discharge_Power_Status |  | 25 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=Reserved, 1=Normal limit POUT, 2=High rate limit POUT, 3= Immediate limit POUT; 00b = Reserved 01b = Normal limit POUT 10b = High rate limit POUT 11b = Immediate limit POUT |
| LB_Voltage_Latch_Flag |  | 24 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK |  |
| LB_Usable_SOC |  | 32 | 7 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Contains SOC for dash. LB_USABLE_SOC is a 1% resolution 'proper' SOC calculated by the LBC and used by ABB chargers and the SOC display in the dash menu as well as the 3-light charging indicator on top of the dash [Needs to be copied to this location when doing 40/62kWh swaps into AZE0] |
| LB_PRUN_1DB |  | 48 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | MPR1DB Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| CRC_1DB |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | CRC | CRC |

### 0x1DC — x1DC (DLC 8, TX HVBAT, 10ms)
*Message notes:* Lithium Battery Controller (10ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LB_Discharge_Power_Limit |  | 7 | 10 | BE | N | 0.25 | 0.0 | 0.0..254.0 | kW | Max available power that can be pulled from battery |
| LB_Charge_Power_Limit |  | 13 | 10 | BE | N | 0.25 | 0.0 | 0.0..254.0 | kW | Max power that battery can be charged with |
| LB_MAX_POWER_FOR_CHARGER |  | 19 | 10 | BE | N | 0.1 | -10.0 | -10.0..90.0 | kW | LB_BPCMAX |
| LB_Charge_Power_Status |  | 24 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=Reserved, 1=Normal limit PIN, 2=High rate limit PIN, 3=Immediate limit PIN; 00b = Reserved 01b = Normal limit PIN 10b = High rate limit PIN 11b = Immediate limit PIN |
| LB_BPCMAX_UPRATE |  | 37 | 3 | LE | N | 1.0 | 0.0 | 0.0..0.0 | MODEMASK | 0=BPC MAX Uprate Level 1, 1=BPC MAX Uprate Level 2, 2=BPC MAX Uprate Level 3, 3=BPC MAX Uprate Level 4, 4=BPC MAX Uprate Level 5, 5=BPC MAX Uprate Level 6, 6=BPC MAX Uprate Level 7, 7=BPC MAX Uprate Level 8; BPC MAX Uprate Level 1-8. Dala: (CAN-bridge testing) This value specifies how quickly the VCM follows the requested power in \"LB_MAX_POWER_FOR_CHARGER\".ZE0 Example, if Level 1 is selected and battery requests 45kW of quickcharging power, it will take 8minutes for power to ramp up from 0kW->45kW. If Level 8 is selected, it will take not ramp at all, and just intantaneously follow the requested power. If low level is forced, some quickcharging stations will fail to charge the vehicle, with an error message stating that too low current was demanded. Special notes for AZE0, the newer AZE0 VCM will ramp more aggressively at level 1 compared to ZE0, and no issues with fastcharging even though slow ramp rate is selected. |
| LB_CODE_CONDITION |  | 34 | 3 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| LB_CODE1 |  | 33 | 8 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| LB_CODE2 |  | 41 | 8 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| LB_PRUN_1DC |  | 48 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 |  | Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| CRC_1DC |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | CRC | CRC |

### 0x1F2 — x1F2 (DLC 8, TX VCM, 10ms)
*Message notes:* Vehicle Control Module (10ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| HvBatChargeablePower |  | 1 | 10 | BE | N | 0.1 | -10.0 | 0.0..0.0 |  |  |
| TargetCharge_SOC |  | 7 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | modemask | 0=100%, 1=Deteroiration Restraint 80%; TCSOC |
| Charge_StatusTransitionReqest |  | 21 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | modemask | 0=other, 1=Normal Charge, 2=Quick Charge, 3=Stop Request; CHG_STA_RQ |
| Keep_SOC_Request |  | 20 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | modemask | 0=Normal charge mode (Initial Value), 1=Keep SOC charge mode; KEEP_SOC_REQ When the temperature in the battery pack is low outside the timer charge set time, VCM transmists a keep SOC request signal to LBC via CAN. In this case, the battery is not charged, and only battery heater is activated. |
| PCS_Connector_Detection |  | 17 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 | modemask | 0=other, 1=Vehicle-to-Home mode; PSCONDET |
| MPRUN |  | 48 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| Unknown_498 |  | 63 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | unknown - may indicate charging |
| CSUM_498 |  | 56 | 4 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Checksum. All message nibbles summed together, plus 2. End result in hex is anded with 0xF. |
| VcmMode |  | 47 | 8 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  | Unknown values. Consult provides raw data 0-255 |
| DcDcConverterReqVoltage |  | 31 | 6 | BE | N | 0.1 | -10.0 | 0.0..0.0 | V |  |

### 0x284 — x284 (DLC 8, TX VCM, 20ms)
*Message notes:* ABS module relayed via VCM to EV-CAN (20ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LeftWheelSpeedSensor |  | 7 | 16 | BE | N | 1.0 | 0.0 | 0.0..65535.0 | pulses | 2's comp |
| RightWheelSpeedSensor |  | 23 | 16 | BE | N | 1.0 | 0.0 | 0.0..65535.0 | pulses | 2's comp |
| AverageRearSpeedSensor |  | 39 | 16 | BE | N | 1.0 | 0.0 | 0.0..65535.0 | pulses | ??? speed sensor.  Maybe average of both rear? Or VehicleSpeedFromABS? |
| DistanceTraveled1 |  | 48 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 |  | 00..ff (wraps ~360 times in a 25 mile drive) |
| DistanceTraveled2 |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 |  | 00..ff (wraps ~360 times in a 25 mile drive) |

### 0x390 — x390 (DLC 8, TX OBCpd, 100ms)
*Message notes:* On Board Charger (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| OBC_Status_AC_Voltage |  | 27 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | status | 0=No Signal, 1=100V, 2=200V, 3=Abnormal Wave; ACVOLST - Type of AC voltage |
| OBC_Flag_QC_Relay_On_Announcemen |  | 38 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 1=Announce OFF, 2=Announce ON; FQCRELAYST - QC Relay announcement |
| OBC_Flag_QC_IR_Sensor |  | 47 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 0=Without, 1=With; FQCIRSENS - QC IR Sensor |
| OBC_Maximum_Charge_Power_Out |  | 40 | 9 | BE | N | 0.1 | 0.0 | 0.0..50.0 | kW | MAXCHGPOUT - Maximum power supplied by charger in kW |
| PRUN_390 |  | 60 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | PRUN | Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| OBC_Charge_Status |  | 46 | 6 | BE | N | 1.0 | 0.0 | 0.0..0.0 | status | 1=Idle OR QC, 2=Finished, 4=Charging OR interrupted, 8=Idle, 9=Idle, 12=Plugged in waiting on timer; From OVMS code |
| CSUM_390 |  | 56 | 4 | LE | N | 1.0 | 0.0 | 0.0..16.0 | CSUM | Sum of all nibbles (-4) |
| OBC_Charge_Power |  | 0 | 9 | BE | N | 0.1 | 0.0 | 0.0..50.0 | kW | Actual charger output - From OVMS code |
| OBC_SleepEnabled |  | 3 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |
| OBC_DcdcConvStatus |  | 26 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |

### 0x393 — x393 (DLC 8, TX OBCpd, 100ms)
*Message notes:* On Board Charger (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| PRUN_393 |  | 60 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | PRUN | Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| Unknown_393_1 |  | 8 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Dala: 20 while idle, 53 while slowcharging |
| Unknown_393_4 |  | 32 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Dala: Always 20 in logs |
| CSUM_393 |  | 56 | 4 | LE | N | 1.0 | 0.0 | 0.0..0.0 | CSUM | Deviates from other CSUM: (All nibbles summed together)-1=CSUM |

### 0x481 — x481 (DLC 2, TX Vector__XXX, 500ms)
*Message notes:* ??? Unknown sender module (500ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| Unknown_481_1 |  | 0 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Dala: Static 0x40, 0x4b while charging |
| Unknown_481_2 |  | 8 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Dala: Static 0x00 |

### 0x50B — x50B (DLC 7, TX VCM, 100ms)
*Message notes:* Vehicle Control Module (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| DiagMuxOn_VCM |  | 18 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 0=Storage of CAN mute/absent failures not authorized, 1=Storage of CAN mute/absent failures; CANMASK |
| HCM_WakeUpSleepCmd |  | 30 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 0=GoToSleep, 1=reserved, 2=reserved, 3=WakeUp; FirstFrameValueIfAlgorithmNotReady = 3 |
| Batt_Heater_Mail_Send_OK |  | 53 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 0=Mail send NG, 1=Mail send OK |
| VcmActivation |  | 17 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  | 0=NON, 2=READY |

### 0x50C — x50C (DLC 6, TX VCM, 100ms)
*Message notes:* Vehicle Control Module (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| HCM_CLOCK_50C |  | 24 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | PRUN Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| ALU_QUESTION_FOR_LBC |  | 32 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | B2h = first question 5Dh = second question |
| CRC |  | 40 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | CRC | CRC |

### 0x54A — x54A (DLC 8, TX HVAC, 100ms)
*Message notes:* AC Auto Amp (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| CCStatusPlusUnknown |  | 0 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 12,3c- CC Off; a0,da- CC On |
| Unknown_54A_1 |  | 8 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 00 (80 in 2013+) |
| Unknown_54A_2 |  | 16 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 70 |
| Unknown_54A_3 |  | 24 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 06,0a,0b,0f |
| ClimateControlSetpoint |  | 32 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | This data is only correct while climate control is active. If climate control isswitched OFF or activated by app, byte 4 reads as 0x00. |
| Unknown_54A_5 |  | 40 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 00 |
| Unknown_54A_6 |  | 48 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | 00 |
| AmbientTempAC |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |

### 0x54B — x54B (DLC 8, TX HVAC, 100ms)
*Message notes:* AC Auto Amp (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| ClimateControlStatus1 |  | 0 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | status | 16=CC On, 17=CC Off, 0=CC On, 1=CC Off; 00 CC on, 01 CC off (2013: 0x10 or 0x11) |
| ClimateVentModeTarget |  | 16 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | status | 128=CC OFF, 136=Face only, 144=Face/Feet, 152=Feet only, 160=Feet/Defrost, 168=Defrost only; (face/feet/defrost) |
| ClimateVentModeIntake |  | 24 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | status | 9=Recirculate, 18=Fresh air, 146=Defrost; (recirculating/fresh air/defrost) |
| FanSpeed |  | 35 | 5 | LE | N | 1.0 | 0.0 | 1.0..7.0 | speed | 1<->7  |
| CCButtonPress |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | 0/1 | Alternates after every CC button press, probably to alert A/V to display CC info |

### 0x54C — x54C (DLC 8, TX HVAC, 100ms)
*Message notes:* AC Auto Amp (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| ACEvaporatorTemperature |  | 0 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | 0.25C/bit? | drops with ac on (after short lag).  No change with heater on |
| CC_BackScreenDefrost |  | 9 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | - | 0=Off, 1=On; Data only changes if climate control is activated or deactivated |
| FanVoltage |  | 40 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | 0.05 V/bit | Commanded fan speed is proportional to Voltage |
| OutsideAmbientTemperature |  | 48 | 8 | LE | N | 0.5 | -40.5 | -40.0..60.0 | degC | Ambient temperature. This one has half-degree C resolution and seems to stay within a degree or two of the \"eyebrow\" temp display. |
| CC_ClimateControlStatus |  | 10 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | - | 0=Off, 1=On; Data only changes if climate control is activated or deactivated |
| CC_ACStatus |  | 11 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | - | 0=Off, 1=On; Data only changes if climate control is activated or deactivated |

### 0x54F — x54F (DLC 8, TX HVAC, 100ms)
*Message notes:* AC Auto Amp (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| InteriorIntakeTemp |  | 0 | 8 | LE | N | 0.5 | -14.0 | 0.0..0.0 | degC | Climate control's measurement of temperature inside the car. Subtracting 14 is a bit of a guess worked out by observing how auto climate control reacts when this reaches the target setting. |
| ACPowerConsumption |  | 8 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | 50W/bit? | Rises to a steady value with ac on.  Off immediately with ac off.  No change with heater on |
| ACAutoAmpStatus |  | 46 | 2 | LE | N | 1.0 | 0.0 | 0.0..0.0 | - | location? |
| HeaterPowerConsumption |  | 40 | 6 | LE | N | 1.0 | 0.0 | 0.0..0.0 | 300W/bit? | Goes up slowly with heater on; no change with ac on |

### 0x55A — x55A (DLC 8, TX INVmc, 100ms)
*Message notes:* Inverter (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| MotorTemperature |  | 32 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 | dC*2 |  |
| InverterComBoardTemp |  | 8 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 | dC*2 | Inverter communications board temp |
| IGBTTemperature |  | 16 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 | dC*2 |  |
| IGBTDriverBoardTemperature |  | 29 | 6 | BE | N | 1.0 | 0.0 | 0.0..255.0 | dC*2 | Temperature only active during drive (IGBT driver board?) |
| INV_SleepEnabled |  | 60 | 2 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |

### 0x55B — x55B (DLC 8, TX HVBAT, 100ms)
*Message notes:* Lithium Battery Controller (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LB_SOC |  | 7 | 10 | BE | N | 1.0 | 0.0 | 0.0..1000.0 | %+1 | State of charge. LB_SOC is a 0.1% resolution SOC that is used on startup by Leaf Spy Pro and then ignored in favor of 0x7BB groups, and seemingly used nowhere in the car |
| LB_ALU_ANSWER |  | 16 | 8 | LE | N | 1.0 | 0.0 | 85.0..170.0 |  |  |
| LB_IR_Sensor_Wave_Voltage |  | 39 | 10 | BE | N | 1.0 | 0.0 | 0.0..4990.0 | mV (5000/1024) | Internal resistance wave voltage |
| LB_IR_Sensor_Malfunction |  | 40 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | modemask | 0=Normal, 1=Malfunction |
| LB_Capacity_Empty |  | 55 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | modemask | 0=Not Empty, 1=Battery Empty |
| LB_SleepEnabled |  | 53 | 2 | BE | N | 1.0 | 0.0 | 0.0..3.0 | modemask | 0=Reserved, 1=RefuseToSleep, 2=ReadyToSleep, 3=Reserved |
| LB_PRUN_55B |  | 48 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 |  | Detection of frozen data. Message-PRUN-Diag. The transmitting node adds a message counter of 2bits or more to the end of the last data area (or just before the checksum). The value of the counter, which is initially 0, increments by one everytime new data is transmitted, and returned to zero when reaching the max value. The receiving node lets the first message pass without check, but for second next message and following, it check whether the counter number is different from the previous message. |
| CRC_55B |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 | CRC | CRC |

### 0x59E — x59E (DLC 8, TX HVBAT, 500ms)
*Message notes:* Lithium Battery Controller (500ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LB_Full_Capacity_for_QC |  | 20 | 9 | BE | N | 100.0 | 0.0 | 0.0..50000.0 | Wh |  |
| LB_Remain_Capacity_for_QC |  | 27 | 9 | BE | N | 100.0 | 0.0 | 0.0..50000.0 | Wh |  |

### 0x5B9 — x5B9 (DLC 7, TX VCM, 500ms)
*Message notes:* Vehicle Control Module (500ms) (Only env200 and USDM LEAF?)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| ActiveFuelBars |  | 3 | 5 | LE | N | 1.0 | 0.0 | 0.0..12.0 | - | From VCM->Cluster |
| ChargeMinutesRemaining |  | 2 | 11 | BE | N | 1.0 | 0.0 | 0.0..2047.0 | minutes |  |
| ChargeTime_100V |  | 18 | 11 | BE | N | 1.0 | 0.0 | 0.0..0.0 |  |  |

### 0x5BC — x5BC (DLC 8, TX HVBAT, 100ms)
*Message notes:* Lithium Battery Controller (100ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LB_Remain_Capacity_GIDS |  | 7 | 10 | BE | N | 1.0 | 0.0 | 0.0..500.0 | gids |  |
| LB_Remaining_Capacity_Segments |  | 16 | 8 | LE | N | 1.0 | 0.0 | 0.0..240.0 |  | Contains chargebars and capacitybars, alternating depending on mux. Simplified lower down in the message |
| LB_Temperature_Segment_For_Dash |  | 24 | 8 | LE | N | 0.4166666 | 0.0 | 0.0..100.0 | % | For instrumentation cluster. Unit is %, times 5/12 according to documentation, kinda strange .Should be average of the 3 sensors inside the battery pack. |
| LB_Capacity_Deterioration_Rate |  | 33 | 7 | LE | N | 1.0 | 0.0 | 0.0..100.0 | % | SOH (State-of-Health) Effects the charge gauge, lower numbers mean more chargebars |
| LB_Remain_Cap_Segment_Swit_Flag |  | 32 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 0=Remaining capacity, 1=Full capacity |
| LB_Output_Power_Limit_Reason |  | 45 | 3 | LE | N | 1.0 | 0.0 | 0.0..7.0 | modemask | 0=Normal, 1=Capacity drop, 2=LBC Malfunction, 3=High temperature, 4=Low temperature, 5=reserved, 6=reserved, 7=reserved; Indicates why power is limited |
| LB_Remain_Charge_Time_Condition |  | 41 | 5 | BE | N | 1.0 | 0.0 | 0.0..30.0 | modemask | 0=Quickcharge, 5=Normal Charge 6kW Full, 8=Normal Charge 200V Full, 11=Normal Charge 100V Full, 18=Normal Charge 6kW 80%, 21=Normal Charge 200V 80%, 24=Normal Charge 100V 80% |
| LB_Remain_Charge_Time |  | 52 | 13 | BE | N | 1.0 | 0.0 | 0.0..8190.0 | minutes | 1FFFh is used as \"Unavailable value\" |
| Mux_5BC | M | 32 | 4 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Multiplexor |
| LB_MaxGIDS |  | 44 | 1 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Only 30kWh AZE0 has this. When this value is 1, the GIDS number is at its maximum.(LB_Remain_Capacity_GIDS) |

### 0x5C0 — x5C0 (DLC 8, TX HVBAT, 500ms)
*Message notes:* Lithium Battery Controller (500ms)

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| LB_Historical_Data_Swich_Flag | M | 6 | 2 | LE | N | 1.0 | 0.0 | 0.0..3.0 | mux | 0=Not Calculated, 1=Maximum Data, 2=Average Data, 3=Minimum Data; Mux for historical data signals |
| LB_Heating_Start_Send_Request |  | 5 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 0->1 Heat start Mail send request |
| LB_Heating_Stop_Send_Request |  | 4 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 0->1 Heat stop Mail send request |
| Batt_Heater_Mail_Send_Request |  | 8 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 0=No request, 1=Mail send request |
| LB_HEATEXIST |  | 32 | 1 | LE | N | 1.0 | 0.0 | 0.0..1.0 | status | 0=Without Battery Heating, 1=With Battery Heating; Specifies if battery pack is equipped with heating elements |
| LB_NextWakeupTimeForBatterHeater |  | 48 | 5 | LE | N | 1.0 | 0.0 | 0.0..1800.0 | minutes |  |
| LB_Diagnosis_Trouble_Code |  | 56 | 8 | LE | N | 1.0 | 0.0 | 0.0..255.0 | DTC |  |

### 0x60D — x60D (DLC 8, TX Vector__XXX, 100ms)
*Message notes:* ??? e-NV200 only, Unknown sender module (100ms) 

| Signal | Mux | Start|Len | Endian | Signed | Scale | Offset | Min..Max | Unit | Enum/Notes |
|---|---:|---:|---:|---|:---:|---:|---:|---|---|---|
| Unknown_60D_0 |  | 0 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Values seen: 00 08 10 14 |
| Unknown_60D_1 |  | 8 | 8 | LE | N | 1.0 | 0.0 | 0.0..0.0 |  | Values seen: 00 04 06 |
