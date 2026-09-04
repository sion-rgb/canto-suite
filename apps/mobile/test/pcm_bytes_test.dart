import 'dart:typed_data';

import 'package:canto_meet/core/pcm_bytes.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('PCM16 conversion accepts odd-offset platform-channel slices', () {
    final backing = Uint8List.fromList([0xff, 0x34, 0x12, 0xcc, 0xff]);
    final oddOffset = Uint8List.sublistView(backing, 1, 5);

    final samples = pcm16LittleEndianView(oddOffset);

    expect(samples, [0x1234, -52]);
  });

  test('PCM16 conversion rejects incomplete samples', () {
    expect(() => pcm16LittleEndianView(Uint8List(3)),
        throwsA(isA<FormatException>()));
  });
}
