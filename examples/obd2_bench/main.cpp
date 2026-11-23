#include <Arduino.h>
#include <SPI.h>
#include "driver/twai.h"
#include "mcp2515.h"
#include "pin_config.h"
#include "vw_firmware_version.h"

#if !defined(T_2Can) || defined(T_2Can_Fd)
#error "obd2_bench requires T-2CAN v1.0 (MCP2515)"
#endif

namespace {
constexpr uint32_t kRequestId = 0x7DF;
constexpr uint32_t kReplyId = 0x7E8;
constexpr uint8_t kMode = 0x01;
constexpr uint8_t kReplyMode = 0x41;
constexpr uint8_t kRpm = 0x0C;
constexpr uint8_t kCoolant = 0x05;
constexpr uint32_t kTimeoutMs = 300;

MCP2515 canA(MCP2515_CS, 10000000, &SPI);

bool startCanA()
{
    digitalWrite(MCP2515_RST, LOW);
    delay(10);
    digitalWrite(MCP2515_RST, HIGH);
    delay(50);
    return canA.reset() == MCP2515::ERROR_OK &&
           canA.setBitrate(CAN_500KBPS, MCP_16MHZ) == MCP2515::ERROR_OK &&
           canA.setNormalMode() == MCP2515::ERROR_OK;
}

bool exchangePid(uint8_t pid, uint8_t replyLength, twai_message_t &reply)
{
    twai_message_t request = {};
    request.identifier = kRequestId;
    request.data_length_code = 8;
    request.ss = 1;
    request.data[0] = 2;
    request.data[1] = kMode;
    request.data[2] = pid;
    if (twai_transmit(&request, 0) != ESP_OK)
    {
        Serial.println("CAN B: could not queue request");
        return false;
    }

    // A replies only after receiving this test's request across the jumpers.
    can_frame incoming = {};
    const uint32_t start = millis();
    while (millis() - start < kTimeoutMs)
    {
        if (canA.readMessage(&incoming) == MCP2515::ERROR_OK &&
            incoming.can_id == kRequestId && incoming.can_dlc == 8 &&
            incoming.data[0] == 2 && incoming.data[1] == kMode &&
            incoming.data[2] == pid)
        {
            can_frame response = {};
            response.can_id = kReplyId;
            response.can_dlc = 8;
            response.data[0] = replyLength;
            response.data[1] = kReplyMode;
            response.data[2] = pid;
            response.data[3] = pid == kRpm ? 0x0C : 0x82;
            response.data[4] = pid == kRpm ? 0x80 : 0;
            if (canA.sendMessage(&response) != MCP2515::ERROR_OK)
            {
                Serial.println("CAN A: could not queue simulated reply");
                return false;
            }

            const uint32_t replyStart = millis();
            while (millis() - replyStart < kTimeoutMs)
            {
                if (twai_receive(&reply, pdMS_TO_TICKS(10)) == ESP_OK &&
                    !reply.extd && !reply.rtr && reply.identifier == kReplyId &&
                    reply.data_length_code == 8 && reply.data[0] == replyLength &&
                    reply.data[1] == kReplyMode && reply.data[2] == pid)
                {
                    return true;
                }
            }
            Serial.println("CAN B: timed out waiting for simulated reply");
            return false;
        }
        delay(1);
    }
    Serial.println("CAN A: timed out waiting for request; check A/B jumpers");
    return false;
}

bool runQueries()
{
    twai_message_t reply = {};
    if (!exchangePid(kRpm, 4, reply))
        return false;
    const uint16_t rpmRaw = (static_cast<uint16_t>(reply.data[3]) << 8) | reply.data[4];
    Serial.printf("Simulated RPM: %.2f rpm\n", rpmRaw / 4.0);

    if (!exchangePid(kCoolant, 3, reply))
        return false;
    const int coolant = static_cast<int>(reply.data[3]) - 40;
    Serial.printf("Simulated coolant: %d C\n", coolant);
    return rpmRaw == 3200 && coolant == 90;
}

void runBenchTest()
{
    twai_general_config_t canB = TWAI_GENERAL_CONFIG_DEFAULT(
        static_cast<gpio_num_t>(CAN_TX), static_cast<gpio_num_t>(CAN_RX), TWAI_MODE_NORMAL);
    twai_timing_config_t timing = TWAI_TIMING_CONFIG_500KBITS();
    twai_filter_config_t filter = TWAI_FILTER_CONFIG_ACCEPT_ALL();

    bool passed = false;
    if (!startCanA())
    {
        Serial.println("CAN A: initialization failed");
    }
    else if (twai_driver_install(&canB, &timing, &filter) != ESP_OK)
    {
        Serial.println("CAN B: driver installation failed");
    }
    else
    {
        if (twai_start() == ESP_OK)
        {
            passed = runQueries();
            twai_stop();
        }
        else
        {
            Serial.println("CAN B: could not start driver");
        }
        twai_driver_uninstall();
    }
    digitalWrite(MCP2515_RST, LOW);
    Serial.println(passed ? "OBD-II BENCH PASS" : "OBD-II BENCH FAIL");
}
} // namespace

void setup()
{
    pinMode(MCP2515_RST, OUTPUT);
    digitalWrite(MCP2515_RST, LOW);
    SPI.begin(MCP2515_SCLK, MCP2515_MISO, MCP2515_MOSI, MCP2515_CS);
    Serial.begin(115200);
    Serial.println("OBD-II BENCH ONLY, DO NOT CONNECT TO VEHICLE. Press t to test or v for version.");
    Serial.println(VW_FIRMWARE_ID);
}

void loop()
{
    const int command = Serial.read();
    if (command == 't' || command == 'T')
        runBenchTest();
    else if (command == 'v' || command == 'V')
        Serial.println(VW_FIRMWARE_ID);
    delay(10);
}
