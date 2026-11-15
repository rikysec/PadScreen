import CoreMedia

/// Trasforma i pacchetti H.264 ricevuti dal PC in CMSampleBuffer pronti per la decodifica hardware.
/// Accetta sia NAL con prefisso di lunghezza (AVCC) sia flussi Annex B, e SPS/PPS sia nell'avcC sia in-band.
struct H264Depacketizer {
    private(set) var format: CMVideoFormatDescription?
    private var sps: [UInt8] = []
    private var pps: [UInt8] = []

    struct Frame {
        let sample: CMSampleBuffer?
        let formatChanged: Bool
        let isIDR: Bool
    }

    /// Applica un record di configurazione (avcC, o SPS/PPS in Annex B). Ritorna true se il formato è cambiato.
    mutating func applyConfig(_ d: [UInt8]) -> Bool {
        var newSPS: [UInt8]?
        var newPPS: [UInt8]?
        if d.count > 6 && d[0] == 1 {
            var i = 5
            let numSPS = Int(d[i] & 0x1F); i += 1
            for _ in 0..<numSPS {
                guard i + 2 <= d.count else { break }
                let l = Int(d[i]) << 8 | Int(d[i + 1]); i += 2
                guard i + l <= d.count else { break }
                if newSPS == nil { newSPS = Array(d[i..<(i + l)]) }
                i += l
            }
            if i < d.count {
                let numPPS = Int(d[i]); i += 1
                for _ in 0..<numPPS {
                    guard i + 2 <= d.count else { break }
                    let l = Int(d[i]) << 8 | Int(d[i + 1]); i += 2
                    guard i + l <= d.count else { break }
                    if newPPS == nil { newPPS = Array(d[i..<(i + l)]) }
                    i += l
                }
            }
        } else {
            for nal in Self.splitAnnexB(d[...]) {
                guard let h = nal.first else { continue }
                switch h & 0x1F {
                case 7: newSPS = Array(nal)
                case 8: newPPS = Array(nal)
                default: break
                }
            }
        }
        return update(sps: newSPS ?? sps, pps: newPPS ?? pps)
    }

    /// Costruisce il sample di un fotogramma. `start` è l'offset dei dati H.264 dentro `d`.
    mutating func makeFrame(_ d: [UInt8], from start: Int, annexB: Bool) -> Frame {
        let body = d[start...]
        let nals = annexB ? Self.splitAnnexB(body) : Self.splitAVCC(body)
        var newSPS: [UInt8]?
        var newPPS: [UInt8]?
        var isIDR = false
        var out = [UInt8]()
        out.reserveCapacity(body.count + 16)
        for nal in nals {
            guard let h = nal.first else { continue }
            let type = h & 0x1F
            switch type {
            case 7: newSPS = Array(nal)
            case 8: newPPS = Array(nal)
            case 9: continue // access unit delimiter
            default:
                if type == 5 { isIDR = true }
                let n = UInt32(nal.count)
                out.append(UInt8(truncatingIfNeeded: n >> 24))
                out.append(UInt8(truncatingIfNeeded: n >> 16))
                out.append(UInt8(truncatingIfNeeded: n >> 8))
                out.append(UInt8(truncatingIfNeeded: n))
                out.append(contentsOf: nal)
            }
        }
        var changed = false
        if newSPS != nil || newPPS != nil {
            changed = update(sps: newSPS ?? sps, pps: newPPS ?? pps)
        }
        guard let format, !out.isEmpty else {
            return Frame(sample: nil, formatChanged: changed, isIDR: isIDR)
        }
        return Frame(sample: Self.makeSampleBuffer(out, format), formatChanged: changed, isIDR: isIDR)
    }

    private mutating func update(sps s: [UInt8], pps p: [UInt8]) -> Bool {
        guard !s.isEmpty, !p.isEmpty else { return false }
        if format != nil && s == sps && p == pps { return false }
        var fd: CMFormatDescription?
        let status: OSStatus = s.withUnsafeBufferPointer { sp in
            p.withUnsafeBufferPointer { pp in
                let ptrs: [UnsafePointer<UInt8>] = [sp.baseAddress!, pp.baseAddress!]
                let sizes: [Int] = [s.count, p.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: ptrs,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &fd)
            }
        }
        guard status == noErr, let fd else { return false }
        sps = s
        pps = p
        format = fd
        return true
    }

    // MARK: - Parsing NAL

    static func splitAVCC(_ s: ArraySlice<UInt8>) -> [ArraySlice<UInt8>] {
        var result: [ArraySlice<UInt8>] = []
        var i = s.startIndex
        let end = s.endIndex
        while i + 4 <= end {
            let len = Int(s[i]) << 24 | Int(s[i + 1]) << 16 | Int(s[i + 2]) << 8 | Int(s[i + 3])
            i += 4
            guard len > 0, i + len <= end else { break }
            result.append(s[i..<(i + len)])
            i += len
        }
        return result
    }

    static func splitAnnexB(_ s: ArraySlice<UInt8>) -> [ArraySlice<UInt8>] {
        var result: [ArraySlice<UInt8>] = []
        var i = s.startIndex
        let end = s.endIndex
        var nalStart: Int?
        while i + 2 < end {
            if s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1 {
                if let st = nalStart {
                    var e = i
                    while e > st && s[e - 1] == 0 { e -= 1 }
                    if e > st { result.append(s[st..<e]) }
                }
                i += 3
                nalStart = i
            } else {
                i += 1
            }
        }
        if let st = nalStart, st < end {
            result.append(s[st..<end])
        }
        return result
    }

    static func makeSampleBuffer(_ data: [UInt8], _ format: CMVideoFormatDescription) -> CMSampleBuffer? {
        var block: CMBlockBuffer?
        var st = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: data.count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: data.count,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &block)
        guard st == noErr, let block else { return nil }
        st = data.withUnsafeBytes { raw in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: data.count)
        }
        guard st == noErr else { return nil }

        var sample: CMSampleBuffer?
        var size = data.count
        st = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: block,
            formatDescription: format,
            sampleCount: 1,
            sampleTimingEntryCount: 0,
            sampleTimingArray: nil,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &size,
            sampleBufferOut: &sample)
        guard st == noErr, let sample else { return nil }

        if let arr = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true), CFArrayGetCount(arr) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(arr, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(
                dict,
                Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        return sample
    }
}
