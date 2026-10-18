#pragma once
#include <cstdint>
#include <cstring>

namespace Dial {
// Send full-width, contiguous changed row bands. Each source slice therefore
// has the same stride as the panel transfer; unchanged pixels are not cleared.
// The caller commits the previous frame only after every transfer completes.
template <typename Send>
uint32_t changedBands(const uint16_t* current, const uint16_t* previous,
                      unsigned width, unsigned height, unsigned bandHeight, Send send) {
    if (!current || !width || !height || !bandHeight) return 0;
    unsigned start = height;
    uint32_t pixels = 0;
    for (unsigned row = 0; row < height; row += bandHeight) {
        const unsigned rows = height - row < bandHeight ? height - row : bandHeight;
        const bool changed = !previous || std::memcmp(current + row * width, previous + row * width,
                                                      rows * width * sizeof(uint16_t)) != 0;
        if (changed && start == height) start = row;
        if (!changed && start != height) {
            send(start, row - start); pixels += (row - start) * width; start = height;
        }
    }
    if (start != height) { send(start, height - start); pixels += (height - start) * width; }
    return pixels;
}
} // namespace Dial
