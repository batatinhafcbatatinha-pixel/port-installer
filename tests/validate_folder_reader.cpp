#include "../runtime/extracted_disc.h"
#include <array>
#include <iostream>
#include <sstream>
int main(int argc, char** argv) {
    if(argc != 2) return 2;
    const auto root = std::filesystem::u8path(argv[1]);
    port_install::ExtractedDisc disc;
    if(!disc.open(root)) { std::cerr << "Cannot open extracted layout\n"; return 3; }
    std::ifstream map(root / ".disc/layout.tsv");
    std::string line; std::getline(map,line);
    size_t checked=0;
    while(std::getline(map,line)) {
        std::istringstream row(line);
        uint64_t start=0,length=0,fileOffset=0;
        row >> start >> length >> fileOffset; row.get();
        std::string name; std::getline(row,name);
        if(!name.empty() && name.back()=='\r') name.pop_back();
        if(!length) continue;
        const uint64_t points[] = {0, length/2, length>4096?length-4096:0};
        for(const uint64_t point: points) {
            const auto count=static_cast<size_t>(std::min<uint64_t>(4096,length-point));
            std::array<uint8_t,4096> actual{},expected{};
            if(!disc.read(start+point,actual.data(),count)) { std::cerr << "Read failed: "<<name<<"\n"; return 4; }
            std::ifstream file(root/std::filesystem::u8path(name),std::ios::binary);
            if(!file) return 5;
            file.seekg(static_cast<std::streamoff>(fileOffset+point));
            file.read(reinterpret_cast<char*>(expected.data()),count);
            if(std::memcmp(actual.data(),expected.data(),count)) { std::cerr << "Mismatch: "<<name<<"\n"; return 6; }
            ++checked;
        }
    }
    std::cout << checked << " sampled ranges matched ordinary extracted files; no ISO opened\n";
    return 0;
}
