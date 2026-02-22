"""
BitChomper - reads arbitrary numbers of bits from a byte array.
Translated from BitChomper.java by John Glassmyer.
"""

import sys


class BitChomper:
    """
    Reads 1-16 bits at a time from a byte array, in either
    LITTLE_ENDIAN or BIG_ENDIAN bit order.
    """

    def __init__(self, data: bytes, starting_byte_offset: int,
                 starting_bit_offset: int, big_endian: bool = False):
        """
        :param data: source byte array
        :param starting_byte_offset: byte index to start reading from
        :param starting_bit_offset: bit index within that byte (0-7)
        :param big_endian: True for big-endian bit order, False for little-endian
        """
        assert 0 <= starting_byte_offset, "byte offset must be >= 0"
        assert 0 <= starting_bit_offset < 8, "bit offset must be 0-7"

        self._data = data
        self._big_endian = big_endian
        self._byte_offset = starting_byte_offset
        self._bit_offset = starting_bit_offset

    @property
    def byte_offset(self) -> int:
        return self._byte_offset

    @property
    def bit_offset(self) -> int:
        return self._bit_offset

    def has_remaining(self, number_of_bits: int) -> bool:
        """Return True if at least number_of_bits bits remain."""
        total_bits = len(self._data) * 8
        consumed = self._byte_offset * 8 + self._bit_offset
        return (total_bits - consumed) >= number_of_bits

    def chomp(self, number_of_bits: int) -> int:
        """
        Read and return the next number_of_bits (1-16) bits as an unsigned int.
        """
        assert 0 < number_of_bits <= 16, "number_of_bits must be 1-16"

        value = 0
        bits_filled = 0

        while bits_filled < number_of_bits:
            bits_needed = number_of_bits - bits_filled
            current_byte = self._data[self._byte_offset] & 0xFF
            bits_from_current_byte = min(bits_needed, 8 - self._bit_offset)

            if self._big_endian:
                mask_shift = 8 - self._bit_offset - bits_from_current_byte
                value_shift = bits_needed - bits_from_current_byte
            else:
                mask_shift = self._bit_offset
                value_shift = bits_filled

            mask = ((1 << bits_from_current_byte) - 1) << mask_shift
            value_from_byte = (current_byte & mask) >> mask_shift
            value |= value_from_byte << value_shift

            bits_filled += bits_from_current_byte
            self._bit_offset += bits_from_current_byte

            if self._bit_offset == 8:
                self._byte_offset += 1
                self._bit_offset = 0

        return value
