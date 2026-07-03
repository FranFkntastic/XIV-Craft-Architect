const preparedFiles = new Map();

export function prepareTextFileSave(key, fileName, content, contentType) {
    preparedFiles.set(key, {
        fileName,
        content,
        contentType: contentType || 'application/octet-stream'
    });

    window.CraftArchitectFileExport = window.CraftArchitectFileExport || {};
    window.CraftArchitectFileExport.savePreparedFile = savePreparedFile;
}

export async function savePreparedFile(key) {
    const preparedFile = preparedFiles.get(key);
    if (!preparedFile) {
        throw new Error(`No prepared file exists for ${key}.`);
    }

    return await saveBlobWithPickerOrDownload(
        preparedFile.fileName,
        preparedFile.content,
        preparedFile.contentType);
}

async function saveBlobWithPickerOrDownload(fileName, content, contentType) {
    const resolvedContentType = contentType || 'application/octet-stream';
    const supportsSavePicker =
        'showSaveFilePicker' in window &&
        (() => {
            try {
                return window.self === window.top;
            } catch {
                return false;
            }
        })();

    if (supportsSavePicker) {
        try {
            const extension = fileName.includes('.')
                ? fileName.slice(fileName.lastIndexOf('.'))
                : '';
            const handle = await window.showSaveFilePicker({
                suggestedName: fileName,
                types: extension
                    ? [{
                        description: 'Diagnostic file',
                        accept: { [resolvedContentType]: [extension] }
                    }]
                    : undefined
            });
            const blob = new Blob([content], { type: resolvedContentType });
            const writable = await handle.createWritable();
            await writable.write(blob);
            await writable.close();
            return { mode: 'picker', canceled: false, fileName };
        } catch (error) {
            if (error && error.name === 'AbortError') {
                return { mode: 'picker', canceled: true, fileName };
            }

            throw error;
        }
    }

    const blob = new Blob([content], { type: resolvedContentType });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
    return { mode: 'download', canceled: false, fileName };
}
