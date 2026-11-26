"""Named friendly ships in a mission file (XWI): the capital ships, transports and shuttles that may speak
their own lines. Read-only; write_casting.py gives each one a speaker from config/ship_voices.yaml."""
import re
import struct

# Craft types, as in the game's XwFlightGroupTypeEnum.
CAPITAL = {12: "freighter", 13: "Mon Cal cruiser", 14: "Nebulon-B frigate", 15: "CR90 corvette"}
SMALL = {8: "transport", 9: "shuttle"}
# IFF 1 is Rebel. IFF 0 means the craft type's default side: taken as friendly only for the Rebel hulls (Mon Cal,
# CR90), so a transport, shuttle or frigate that defaults to Imperial gets no speaker. The plugin checks the side
# again in flight.
FRIENDLY_DEFAULT = {13, 15}
XWI_IFF = 0x34      # FlightGroupFileStruct: offset of the IFF field


def fgs(mission):
    """[(name, craft type, count, IFF)] of every flight group in the mission file; [] if there is no file."""
    import write_casting      # imported here because write_casting imports this module
    return [(name, craft_type, count, struct.unpack_from("<h", record, XWI_IFF)[0])
            for _, record, name, craft_type, count in write_casting.xwi_flight_groups(mission)]


def friendly_ships(mission):
    """[(flight-group name as in the XWI, craft type, count)] of named friendly ships that may talk."""
    friendly = []
    for name, craft_type, count, iff in fgs(mission):
        if count <= 0 or not (craft_type in CAPITAL or craft_type in SMALL):
            continue
        if iff == 1 or (iff == 0 and craft_type in FRIENDLY_DEFAULT):
            friendly.append((name, craft_type, count))
    return friendly


def display(name):
    """'QILUIY' -> 'Qiluiy', "PLEE'H 1" -> "Plee'h 1", 'SOR-SULL II' -> 'Sor-Sull II'."""
    words = []
    for word in name.split():
        if re.fullmatch(r"[IVX]+", word.upper()):       # a Roman numeral stays in capitals
            words.append(word.upper())
        else:
            words.append("-".join(part[:1].upper() + part[1:].lower() for part in word.split("-")))
    return " ".join(words)


def group_name(name):
    """The name a flight group calls itself on the radio: its XWI name in display case ('Qiluiy'). The ships of
    one group (Qiluiy x3) share that name; 'Citadel 1' and 'Citadel 2' are two groups with a name each."""
    return display(name)
