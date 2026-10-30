"""Create isolated PS2 runtime sources; never modify the shared checkout."""
import argparse
from pathlib import Path
import shutil

p = argparse.ArgumentParser()
p.add_argument('--source', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
stubs = a.source / 'src/lib/Kernel/Stubs'
shutil.copytree(stubs, a.output / 'Stubs', dirs_exist_ok=True)
shutil.copyfile(Path(__file__).with_name('extracted_disc.h'), a.output / 'extracted_disc.h')


def replace(text, old, new):
    if text.count(old) != 1:
        raise ValueError('Runtime changed; review extracted-files adaptation: ' + old[:100])
    return text.replace(old, new)


common = a.output / 'Stubs/Common.h'
s = common.read_text(encoding='utf-8-sig')
s = '#include "../extracted_disc.h"\n' + s
common.write_text(s, encoding='utf-8')
helper = a.output / 'Stubs/Helpers/Support.h'
s = helper.read_text(encoding='utf-8-sig')
s = replace(s, '    bool tryGetCdImageTotalSectors(uint64_t &totalSectorsOut)\n    {', '''    port_install::ExtractedDisc& extractedDisc() {
        static port_install::ExtractedDisc disc;
        disc.open(getCdRootPath());
        return disc;
    }

    bool tryGetCdImageTotalSectors(uint64_t &totalSectorsOut)
    {
        auto& folder = extractedDisc();
        if (folder.size()) { totalSectorsOut = folder.size() / kCdSectorSize; return true; }
''')
s = replace(s, '        entry.baseLbn = g_nextPseudoLbn;', '''        entry.baseLbn = g_nextPseudoLbn;
        extractedDisc().fileLbn(path, entry.baseLbn);''')
s = replace(s, '    bool readCdSectors(uint32_t lbn, uint32_t sectors, uint8_t *dst, size_t byteCount)\n    {', '''    bool readCdSectors(uint32_t lbn, uint32_t sectors, uint8_t *dst, size_t byteCount)
    {
        auto& folder = extractedDisc();
        if (folder.size()) {
            static unsigned traced = 0;
            if (traced++ < 16) std::cerr << "[extracted-disc] lbn=" << lbn << " sectors=" << sectors << std::endl;
            const bool ok = folder.read(static_cast<uint64_t>(lbn) * kCdSectorSize, dst, byteCount);
            g_lastCdError = ok ? 0 : -1;
            return ok;
        }
''')
s = replace(s, '        return PS2Runtime::getIoPaths().cdImage;', '        return {}; // Extracted-only port: a raw image is never a runtime fallback.')
helper.write_text(s, encoding='utf-8')
host = a.source / 'src/lib/ps2_iop_host.cpp'
s = host.read_text(encoding='utf-8-sig')
s = '#include "extracted_disc.h"\n' + s
needle = '        return paths.cdImage.string();'
s = replace(s, needle, '''        if (std::filesystem::exists(paths.cdRoot / ".disc/layout.tsv"))
            return (paths.cdRoot / ".disc/layout.tsv").string();
''' + '        return {}; // Never expose a raw disc image to the IOP.')
s = replace(s, 'uint64_t PS2IopHostAdapter::openHostFile(std::string_view path)\n{', '''namespace {
    constexpr uint64_t kExtractedHandle = UINT64_MAX;
    port_install::ExtractedDisc& hostExtractedDisc() {
        static port_install::ExtractedDisc disc;
        disc.open(PS2Runtime::getIoPaths().cdRoot);
        return disc;
    }
}
uint64_t PS2IopHostAdapter::openHostFile(std::string_view path)
{
    if (std::filesystem::path(std::string(path)).filename() == "layout.tsv" && hostExtractedDisc().size())
        return kExtractedHandle;
''')
s = replace(s, 'bool PS2IopHostAdapter::hostFileSize(uint64_t handle, uint64_t &size) const\n{', '''bool PS2IopHostAdapter::hostFileSize(uint64_t handle, uint64_t &size) const
{
    if (handle == kExtractedHandle) { size = hostExtractedDisc().size(); return size != 0; }
''')
s = replace(s, '    bytesRead = 0u;\n    if (!destination && size != 0u)', '''    if (handle == kExtractedHandle) {
        const bool ok = hostExtractedDisc().read(offset, destination, size);
        bytesRead = ok ? size : 0;
        return ok;
    }
    bytesRead = 0u;
    if (!destination && size != 0u)''')
s = replace(s, 'void PS2IopHostAdapter::closeHostFile(uint64_t handle)\n{', '''void PS2IopHostAdapter::closeHostFile(uint64_t handle)
{
    if (handle == kExtractedHandle) return;
''')
(a.output / 'ps2_iop_host.cpp').write_text(s, encoding='utf-8')
