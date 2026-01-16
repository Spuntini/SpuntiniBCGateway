# Peppol PDF Extractor

A simple Windows desktop application that extracts embedded PDF files from Peppol UBL documents (.xml or .ubl files).

## Features

- **Multi-file selection**: Select one or more Peppol UBL files at once
- **Batch extraction**: Extract PDFs from all selected files in one operation
- **Smart naming**: PDFs are saved with the naming convention: `{DocumentId}_{OriginalFileName}.pdf`
- **Duplicate handling**: Automatically handles duplicate filenames by appending a counter
- **User-friendly GUI**: Simple and intuitive Windows Forms interface

## Requirements

- .NET 9.0 runtime
- Windows operating system (Windows Forms application)

## Usage

1. Launch the application
2. Click **"Select Files"** to choose one or more Peppol UBL files (.xml or .ubl)
3. Review selected files in the list
4. Click **"Extract PDFs"** to begin extraction
5. Select a destination folder where PDFs will be saved
6. View the extraction results

## Features in Detail

### Buttons

- **Select Files**: Opens a file dialog to select Peppol UBL files
- **Remove Selected**: Removes selected files from the list
- **Clear All**: Clears all selected files
- **Extract PDFs**: Extracts embedded PDFs from all selected files

### File Naming Convention

Output files are named as: `{DocumentId}_{AttachmentName}.pdf`

For example:
- Input: `PEPPOL-INV-001.xml` with Document ID `INV-2024-0001`
- Output: `INV-2024-0001_invoice.pdf`

### Error Handling

The application provides detailed feedback for each file:
- ✓ Successfully extracted PDFs are counted
- ✗ Failed extractions show the reason (no attachment, invalid file, etc.)

## Building

From the solution root:

```bash
dotnet build
```

To run:

```bash
dotnet run --project PeppolPdfExtractor
```

## Dependencies

- **SpuntiniBCGateway**: Uses the `PeppolUblReader` service to parse Peppol documents
- Windows Forms: For the user interface

## Note

This application depends on the SpuntiniBCGateway project, which must be built first. The PeppolUblReader automatically extracts embedded PDFs from Peppol documents based on the Peppol specification.
