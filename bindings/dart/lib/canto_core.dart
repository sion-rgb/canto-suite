library canto_core;

import 'dart:async';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:ffi/ffi.dart';

final class _EngineConfig extends Struct {
  @Uint32()
  external int structSize;
  @Uint32()
  external int sampleRateHz;
  @Uint32()
  external int ringCapacitySamples;
  @Uint32()
  external int resultQueueCapacity;
  @Uint32()
  external int chunkSamples;
}

final class _NativeResult extends Struct {
  @Uint32()
  external int structSize;
  @Int32()
  external int kind;
  @Int64()
  external int startMs;
  @Int64()
  external int endMs;
  external Pointer<Utf8> text;
  @Size()
  external int textLength;
}

typedef _CreateNative = Int32 Function(Pointer<_EngineConfig>, Pointer<Pointer<Void>>);
typedef _CreateDart = int Function(Pointer<_EngineConfig>, Pointer<Pointer<Void>>);
typedef _DestroyNative = Void Function(Pointer<Void>);
typedef _DestroyDart = void Function(Pointer<Void>);
typedef _LoadNative = Int32 Function(Pointer<Void>, Pointer<Utf8>);
typedef _LoadDart = int Function(Pointer<Void>, Pointer<Utf8>);
typedef _PushNative = Int32 Function(Pointer<Void>, Pointer<Int16>, Size, Uint8);
typedef _PushDart = int Function(Pointer<Void>, Pointer<Int16>, int, int);
typedef _PollNative = Int32 Function(Pointer<Void>, Pointer<_NativeResult>);
typedef _PollDart = int Function(Pointer<Void>, Pointer<_NativeResult>);
typedef _FreeResultNative = Void Function(Pointer<_NativeResult>);
typedef _FreeResultDart = void Function(Pointer<_NativeResult>);

enum TranscriptKind { partial, finalResult, error }

class TranscriptResult {
  const TranscriptResult(this.kind, this.startMs, this.endMs, this.text);
  final TranscriptKind kind;
  final int startMs;
  final int endMs;
  final String text;
}

class CantoCoreException implements Exception {
  const CantoCoreException(this.status, this.operation);
  final int status;
  final String operation;
  @override
  String toString() => 'CantoCoreException(status: $status, operation: $operation)';
}

class CantoCoreEngine {
  CantoCoreEngine({this.sampleRateHz = 16000, this.bufferSeconds = 30}) {
    final config = calloc<_EngineConfig>();
    final output = calloc<Pointer<Void>>();
    config.ref
      ..structSize = sizeOf<_EngineConfig>()
      ..sampleRateHz = sampleRateHz
      ..ringCapacitySamples = sampleRateHz * bufferSeconds
      ..resultQueueCapacity = 128
      ..chunkSamples = sampleRateHz * 10;
    final status = _create(config, output);
    calloc.free(config);
    if (status != 0) {
      calloc.free(output);
      throw CantoCoreException(status, 'create');
    }
    _handle = output.value;
    calloc.free(output);
  }

  final int sampleRateHz;
  final int bufferSeconds;
  final DynamicLibrary _library = DynamicLibrary.open(
    Platform.isAndroid ? 'libcanto_core.so' : 'canto_core.dll',
  );
  late final _CreateDart _create = _library.lookupFunction<_CreateNative, _CreateDart>('canto_engine_create');
  late final _DestroyDart _destroy = _library.lookupFunction<_DestroyNative, _DestroyDart>('canto_engine_destroy');
  late final _LoadDart _load = _library.lookupFunction<_LoadNative, _LoadDart>('canto_model_load');
  late final _PushDart _push = _library.lookupFunction<_PushNative, _PushDart>('canto_push_pcm16');
  late final _PollDart _poll = _library.lookupFunction<_PollNative, _PollDart>('canto_poll_result');
  late final _FreeResultDart _freeResult = _library.lookupFunction<_FreeResultNative, _FreeResultDart>('canto_result_free');
  late Pointer<Void> _handle;
  bool _disposed = false;

  void loadModel(String path) {
    final nativePath = path.toNativeUtf8();
    final status = _load(_handle, nativePath);
    calloc.free(nativePath);
    if (status != 0) throw CantoCoreException(status, 'loadModel');
  }

  void push(Int16List samples, {bool endOfStream = false}) {
    final nativeSamples = calloc<Int16>(samples.length);
    nativeSamples.asTypedList(samples.length).setAll(0, samples);
    final status = _push(_handle, nativeSamples, samples.length, endOfStream ? 1 : 0);
    calloc.free(nativeSamples);
    if (status != 0) throw CantoCoreException(status, 'push');
  }

  TranscriptResult? poll() {
    final native = calloc<_NativeResult>();
    native.ref.structSize = sizeOf<_NativeResult>();
    final status = _poll(_handle, native);
    if (status == 5) {
      calloc.free(native);
      return null;
    }
    if (status != 0) {
      calloc.free(native);
      throw CantoCoreException(status, 'poll');
    }
    final result = TranscriptResult(
      switch (native.ref.kind) { 1 => TranscriptKind.partial, 2 => TranscriptKind.finalResult, _ => TranscriptKind.error },
      native.ref.startMs,
      native.ref.endMs,
      native.ref.text == nullptr ? '' : native.ref.text.toDartString(),
    );
    _freeResult(native);
    calloc.free(native);
    return result;
  }

  void dispose() {
    if (_disposed) return;
    _destroy(_handle);
    _disposed = true;
  }
}

sealed class _Command {}
class _Load extends _Command { _Load(this.path); final String path; }
class _Audio extends _Command { _Audio(this.bytes, this.eos); final TransferableTypedData bytes; final bool eos; }
class _Poll extends _Command {}
class _Stop extends _Command {}

/// One persistent isolate owns one native engine for the entire recording.
class CantoCoreWorker {
  CantoCoreWorker._(this._commands, this.results, this._isolate);
  final SendPort _commands;
  final Stream<TranscriptResult> results;
  final Isolate _isolate;

  static Future<CantoCoreWorker> start() async {
    final ready = ReceivePort();
    final resultPort = ReceivePort();
    final isolate = await Isolate.spawn(_workerMain, (ready.sendPort, resultPort.sendPort));
    final commands = await ready.first as SendPort;
    return CantoCoreWorker._(
      commands,
      resultPort.cast<TranscriptResult>().asBroadcastStream(),
      isolate,
    );
  }

  void loadModel(String path) => _commands.send(_Load(path));

  void push(Int16List samples, {bool endOfStream = false}) {
    final bytes = Uint8List.view(samples.buffer, samples.offsetInBytes, samples.lengthInBytes);
    _commands.send(_Audio(TransferableTypedData.fromList([bytes]), endOfStream));
  }

  void poll() => _commands.send(_Poll());

  void dispose() {
    _commands.send(_Stop());
  }
}

void _workerMain((SendPort, SendPort) ports) {
  final receive = ReceivePort();
  final engine = CantoCoreEngine();
  ports.$1.send(receive.sendPort);
  receive.listen((message) {
    try {
      switch (message) {
        case _Load(:final path): engine.loadModel(path);
        case _Audio(:final bytes, :final eos):
          final data = bytes.materialize().asUint8List();
          engine.push(Int16List.view(data.buffer, data.offsetInBytes, data.lengthInBytes ~/ 2), endOfStream: eos);
          TranscriptResult? result;
          while ((result = engine.poll()) != null) { ports.$2.send(result); }
        case _Poll():
          TranscriptResult? result;
          while ((result = engine.poll()) != null) { ports.$2.send(result); }
        case _Stop(): engine.dispose(); receive.close();
      }
    } catch (error) {
      ports.$2.send(TranscriptResult(TranscriptKind.error, 0, 0, error.toString()));
    }
  });
}
