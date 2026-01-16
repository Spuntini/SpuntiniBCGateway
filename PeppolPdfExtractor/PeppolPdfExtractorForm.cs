using System.Windows.Forms;
using SpuntiniBCGateway.Services;

namespace PeppolPdfExtractor;

public partial class PeppolPdfExtractorForm : Form
{
    private List<string> selectedFiles = new();

    public PeppolPdfExtractorForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = "Peppol PDF Extractor";
        this.Size = new System.Drawing.Size(600, 400);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = true;

        // Main panel
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };

        // Title label
        var titleLabel = new Label
        {
            Text = "Peppol PDF Extractor",
            Font = new System.Drawing.Font("Segoe UI", 14, System.Drawing.FontStyle.Bold),
            AutoSize = true
        };
        mainPanel.Controls.Add(titleLabel, 0, 0);

        // Instructions label
        var instructionsLabel = new Label
        {
            Text = "Select one or more Peppol UBL files (.xml or .ubl) to extract embedded PDFs.",
            AutoSize = true,
            Dock = DockStyle.Top
        };
        mainPanel.Controls.Add(instructionsLabel, 0, 1);

        // Files listbox
        var filesListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.MultiExtended,
            Height = 150
        };
        mainPanel.Controls.Add(filesListBox, 0, 2);

        // Buttons panel
        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        var selectButton = new Button
        {
            Text = "Select Files",
            Width = 100,
            Height = 35
        };
        selectButton.Click += (s, e) => SelectFiles(filesListBox);
        buttonsPanel.Controls.Add(selectButton);

        var removeButton = new Button
        {
            Text = "Remove Selected",
            Width = 120,
            Height = 35
        };
        removeButton.Click += (s, e) => RemoveSelectedFiles(filesListBox);
        buttonsPanel.Controls.Add(removeButton);

        var clearButton = new Button
        {
            Text = "Clear All",
            Width = 90,
            Height = 35
        };
        clearButton.Click += (s, e) => ClearAllFiles(filesListBox);
        buttonsPanel.Controls.Add(clearButton);

        var extractButton = new Button
        {
            Text = "Extract PDFs",
            Width = 100,
            Height = 35,
            BackColor = System.Drawing.Color.Green,
            ForeColor = System.Drawing.Color.White
        };
        extractButton.Click += (s, e) => ExtractPdfs();
        buttonsPanel.Controls.Add(extractButton);

        mainPanel.Controls.Add(buttonsPanel, 0, 3);

        this.Controls.Add(mainPanel);
    }

    private void SelectFiles(ListBox filesListBox)
    {
        using (OpenFileDialog openFileDialog = new OpenFileDialog())
        {
            openFileDialog.Filter = "Peppol UBL Files (*.xml;*.ubl)|*.xml;*.ubl|All Files (*.*)|*.*";
            openFileDialog.Multiselect = true;
            openFileDialog.Title = "Select Peppol UBL Files";

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                foreach (var file in openFileDialog.FileNames)
                {
                    if (!selectedFiles.Contains(file))
                    {
                        selectedFiles.Add(file);
                    }
                }

                UpdateFilesList(filesListBox);
            }
        }
    }

    private void RemoveSelectedFiles(ListBox filesListBox)
    {
        var indicesToRemove = new List<int>();

        foreach (int index in filesListBox.SelectedIndices)
        {
            indicesToRemove.Add(index);
        }

        for (int i = indicesToRemove.Count - 1; i >= 0; i--)
        {
            selectedFiles.RemoveAt(indicesToRemove[i]);
        }

        UpdateFilesList(filesListBox);
    }

    private void ClearAllFiles(ListBox filesListBox)
    {
        selectedFiles.Clear();
        UpdateFilesList(filesListBox);
    }

    private void UpdateFilesList(ListBox filesListBox)
    {
        filesListBox.Items.Clear();
        foreach (var file in selectedFiles)
        {
            filesListBox.Items.Add(System.IO.Path.GetFileName(file));
        }
    }

    private void ExtractPdfs()
    {
        if (selectedFiles.Count == 0)
        {
            MessageBox.Show("Please select at least one Peppol UBL file.", "No Files Selected", 
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
        {
            folderBrowserDialog.Description = "Select the destination folder for extracted PDFs";

            if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
            {
                string destinationFolder = folderBrowserDialog.SelectedPath;
                int successCount = 0;
                int failureCount = 0;
                var failedFiles = new List<string>();

                foreach (var filePath in selectedFiles)
                {
                    try
                    {
                        var document = PeppolUblReader.ReadPeppolDocument(filePath);

                        if (document?.Header?.Attachment != null)
                        {
                            var attachment = document.Header.Attachment;
                            if (attachment.FileContent != null && attachment.FileContent.Length > 0)
                            {
                                // Generate filename: DocumentId + AttachmentFileName + .pdf
                                string documentId = document.Header.DocumentId ?? "UNKNOWN";
                                string attachmentFileName = System.IO.Path.GetFileNameWithoutExtension(attachment.FileName) ?? "attachment";
                                string outputFileName = $"{documentId}_{attachmentFileName}.pdf";
                                string outputPath = System.IO.Path.Combine(destinationFolder, outputFileName);

                                // If file exists, add a counter
                                int counter = 1;
                                while (System.IO.File.Exists(outputPath))
                                {
                                    outputPath = System.IO.Path.Combine(destinationFolder, 
                                        $"{documentId}_{attachmentFileName}_{counter}.pdf");
                                    counter++;
                                }

                                System.IO.File.WriteAllBytes(outputPath, attachment.FileContent);
                                successCount++;
                            }
                            else
                            {
                                failureCount++;
                                failedFiles.Add($"{System.IO.Path.GetFileName(filePath)} - No file content");
                            }
                        }
                        else
                        {
                            failureCount++;
                            failedFiles.Add($"{System.IO.Path.GetFileName(filePath)} - No attachment found");
                        }
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        failedFiles.Add($"{System.IO.Path.GetFileName(filePath)} - {ex.Message}");
                    }
                }

                // Show results
                string message = $"Extraction completed:\n\n✓ Successfully extracted: {successCount} PDF(s)\n✗ Failed: {failureCount}";
                if (failedFiles.Count > 0)
                {
                    message += "\n\nFailed files:\n" + string.Join("\n", failedFiles);
                }

                MessageBox.Show(message, "Extraction Results", MessageBoxButtons.OK, 
                    failureCount == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

                // Clear the list after successful extraction
                if (successCount > 0)
                {
                    selectedFiles.Clear();
                    var filesListBox = this.Controls.OfType<TableLayoutPanel>().FirstOrDefault()?.Controls
                        .OfType<ListBox>().FirstOrDefault();
                    if (filesListBox != null)
                    {
                        UpdateFilesList(filesListBox);
                    }
                }
            }
        }
    }
}
