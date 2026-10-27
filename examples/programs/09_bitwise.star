# title: Bitwise operators and shifts
# output: [120, 4294967295, 4294967168, -2, 5, 372, -1]

print([0x12345678 & 0xFF, 0x12345678 | 0xFFFFFFFF, 0xFFFFFFFF ^ 0x7F, ~1, 0b101, 0b01011101 << 2, -1 >> 100])
