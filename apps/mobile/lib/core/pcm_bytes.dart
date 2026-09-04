import 'dart:typed_data';

Int16List pcm16LittleEndianView(Uint8List bytes) {
  if (!bytes.lengthInBytes.isEven) {
    throw const FormatException('PCM16 byte count must be even');
  }
  // StandardMessageCodec may return a typed-data slice whose byte offset is
  // odd. Int16List.view rejects that offset, so copy only in that case.
  final aligned =
      bytes.offsetInBytes.isEven ? bytes : Uint8List.fromList(bytes);
  return Int16List.view(
      aligned.buffer, aligned.offsetInBytes, aligned.lengthInBytes ~/ 2);
}
