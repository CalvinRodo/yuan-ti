// GFF file reader for Dark Sun assets
// Usage: node src/gffReader.js <path-to-gff-file>

const fs = require('fs');

// Map of known chunk types
const GFF_CHUNK_TYPES = {
    'ACCL': 'Accelerator resource',
    'ADV ': 'AIL Audio Driver',
    'APFM': 'Application frame',
    'BMP ': 'Bitmap',
    'BVOC': 'Background play samples',
    'BUTN': 'Button resource',
    'CMAP': 'Color map table',
    'DADV': 'AIL and .COM drivers',
    'DBOX': 'Dialog box resource',
    'DRV ': 'Run-time loadable',
    'EDAT': '3DX Environment database',
    'EBOX': 'Edit box resource',
    'ETAB': 'Object entry table',
    'FONT': 'Font',
    'FVOC': 'Foreground play samples',
    'GMAP': 'Region map flags',
    'GPL ': 'Compiled GPL files',
    'GPLX': 'GPL index file',
    'ICON': 'Icon resource',
    'MAS ': 'Compiled MAS files',
    'MENU': 'Menu resource',
    'MGTL': 'Global timbre library',
    'MSEQ': 'XMIDI sequence files',
    'GSEQ': 'Global audio/sync sequence',
    'LSEQ': 'Local audio sequence',
    'CSEQ': 'Cinematic sequence (cutscene / scripted playback control)',
    'PSEQ': 'Playback sequence (audio/visual sync blocks)',
    'OMAP': 'Opacity map',
    'PAL ': 'VGA 256 color palette',
    'POBJ': 'PolyMesh object database',
    'RMAP': 'Region tile map',
    'SCMD': 'Animation script',
    'SBAR': 'Scroll-bar resource',
    'SINF': 'Sound card info',
    'SJMP': 'OENGINE animation',
    'TEXT': 'Text resource',
    'TILE': 'Tile graphic',
    'TMAP': 'Texture map',
    'TXRF': 'Texture map',
    'WIND': 'Window resource',
    'ACF ': 'Cinematic Binary',
    'BMA ': 'Cinematic Binary File',
};

function readGFFHeader(buffer) {
    // Example: Read first 16 bytes as header (customize as needed)
    return {
        magic: buffer.toString('ascii', 0, 4),
        version: buffer.readUInt32LE(4),
        // Add more fields as needed
    };
}

function readChunkType(buffer, offset) {
    // Reads 4 bytes at offset as chunk type
    return buffer.toString('ascii', offset, offset + 4);
}

function describeChunkType(type) {
    return GFF_CHUNK_TYPES[type] || 'Unknown';
}

function scanGFFChunks(buffer) {
    // This is a placeholder: actual chunk structure may vary by GFF variant
    // Here, we just scan for all 4-byte sequences that match known chunk types
    const found = [];
    for (let i = 0; i < buffer.length - 4; i++) {
        const type = buffer.toString('ascii', i, i + 4);
        if (GFF_CHUNK_TYPES[type]) {
            found.push({ offset: i, type, description: GFF_CHUNK_TYPES[type] });
        }
    }
    return found;
}

function readGFFFile(filePath) {
    const buffer = fs.readFileSync(filePath);
    const header = readGFFHeader(buffer);
    console.log('GFF Header:', header);
    const chunks = scanGFFChunks(buffer);
    console.log('Found chunk types:');
    for (const chunk of chunks) {
        console.log(`  Offset 0x${chunk.offset.toString(16)}: ${chunk.type} (${chunk.description})`);
    }
}

if (require.main === module) {
    const filePath = process.argv[2];
    if (!filePath) {
        console.error('Usage: node src/gffReader.js <path-to-gff-file>');
        process.exit(1);
    }
    readGFFFile(filePath);
}

module.exports = { readGFFFile, readGFFHeader, scanGFFChunks, describeChunkType };