#include "../runtime/extracted_disc.h"
#include <array>
#include <iostream>
#include <string>
int main(int argc, char** argv) {
    if (argc != 2) return 2;
    port_install::ExtractedDisc disc;
    if (!disc.open(argv[1])) return 3;
    std::array<uint8_t, 2048> pvd{};
    if (!disc.read(16*2048, pvd.data(), pvd.size()) || pvd[0] != 1 || std::memcmp(pvd.data()+1, "CD001",5)) return 4;
    uint32_t lbn=0;
    if (!disc.fileLbn(std::filesystem::path(argv[1])/"GAME.ELF", lbn)) return 5;
    std::array<uint8_t, 2048> data{};
    if (!disc.read(uint64_t(lbn)*2048,data.data(),data.size())) return 6;
    const std::string expected="game executable fixture";
    if (std::memcmp(data.data(),expected.data(),expected.size())) return 7;
    for(size_t i=expected.size(); i<data.size(); ++i) if(data[i]) return 8;
    std::array<uint8_t, 32> cross{};
    if(!disc.read(uint64_t(lbn)*2048-1,cross.data(),cross.size()) || cross[0] || std::memcmp(cross.data()+1,expected.data(),expected.size())) return 9;
    if(disc.read(disc.size()-1,data.data(),2)) return 10;
    std::cout << "Folder-backed PVD, file, cross-extent read, sector padding and bounds: OK\n";
}
