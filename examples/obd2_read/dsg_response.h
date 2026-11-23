#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

class DsgResponse
{
public:
    enum class Result { Ignore, FlowControl, Receiving, Complete, Pending, Negative, Invalid };
    static constexpr size_t capacity = 96;

    Result accept(uint16_t did, const uint8_t *data, size_t dlc)
    {
        if (dlc == 0 || dlc > 8)
            return Result::Ignore;

        const uint8_t frameType = data[0] >> 4;
        if (received != 0)
        {
            if (frameType != 2)
                return Result::Ignore;
            const size_t remaining = length - received;
            const size_t count = remaining < 7 ? remaining : 7;
            if ((data[0] & 0x0F) != sequence || dlc < count + 1)
                return Result::Invalid;

            std::memcpy(payload + received, data + 1, count);
            received += count;
            sequence = (sequence + 1) & 0x0F;
            return received == length ? Result::Complete : Result::Receiving;
        }

        if (frameType == 0 && dlc >= 4)
        {
            const size_t size = data[0] & 0x0F;
            if (size == 3 && data[1] == 0x7F && data[2] == 0x22)
            {
                nrc = data[3];
                return nrc == 0x78 ? Result::Pending : Result::Negative;
            }
            if (!matchesDid(did, data + 1))
                return Result::Ignore;
            if (size < 4 || size >= dlc)
                return Result::Invalid;

            length = size;
            std::memcpy(payload, data + 1, length);
            return Result::Complete;
        }

        if (frameType == 1 && dlc >= 5 && matchesDid(did, data + 2))
        {
            length = (size_t(data[0] & 0x0F) << 8) | data[1];
            if (dlc != 8 || length <= 7 || length > capacity)
                return Result::Invalid;

            received = dlc - 2;
            std::memcpy(payload, data + 2, received);
            return Result::FlowControl;
        }
        return Result::Ignore;
    }

    uint8_t payload[capacity] = {};
    size_t length = 0;
    uint8_t nrc = 0;

private:
    static bool matchesDid(uint16_t did, const uint8_t *data)
    {
        return data[0] == 0x62 && data[1] == (did >> 8) && data[2] == (did & 0xFF);
    }

    size_t received = 0;
    uint8_t sequence = 1;
};
