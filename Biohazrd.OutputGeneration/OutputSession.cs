﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Biohazrd.OutputGeneration
{
    public sealed class OutputSession : IDisposable
    {
        private string _BaseOutputDirectory;
        public string BaseOutputDirectory
        {
            get => _BaseOutputDirectory;
            set => _BaseOutputDirectory = Path.GetFullPath(value);
        }

        public bool AutoRenameConflictingFiles { get; set; } = true;

        private Dictionary<Type, Delegate> Factories = new();

        private Dictionary<string, object> Writers = new();

        private HashSet<string> _FilesWrittenMoreThanOnce = new();

        public IReadOnlyCollection<string> FilesWritten => Writers.Keys;
        public IReadOnlyCollection<string> FilesWrittenMoreThanOnce => _FilesWrittenMoreThanOnce;

        private string? _GeneratedFileHeader;
        private string[] _GeneratedFileHeaderLines;
        public string? GeneratedFileHeader
        {
            get => _GeneratedFileHeader;
            set
            {
                if (String.IsNullOrEmpty(value))
                {
                    _GeneratedFileHeader = null;
                    _GeneratedFileHeaderLines = Array.Empty<string>();
                }
                else
                {
                    _GeneratedFileHeader = value.Replace("\r", "");
                    _GeneratedFileHeaderLines = value.Split('\n');
                }
            }
        }
        public ReadOnlySpan<string> GeneratedFileHeaderLines => _GeneratedFileHeaderLines;
        public bool HasGeneratedFileHeader => GeneratedFileHeader is not null;

        public OutputSession()
        {
            _GeneratedFileHeaderLines = null!; // This is initialized when GeneratedFileHeader is set.
            GeneratedFileHeader = $"This file was automatically generated by {nameof(Biohazrd)} and should not be modified by hand!";
            _BaseOutputDirectory = null!; // This is initialized when BaseOutputDirectory is set.
            BaseOutputDirectory = Environment.CurrentDirectory;

            // Add default factories
            AddFactory((session, path) => new StreamWriter(path));
            AddFactory((session, path) => new FileStream(path, FileMode.Create));
            AddFactory((session, path) => new ReserveWithoutOpening(path));
        }

        public void WriteHeader(TextWriter writer, string linePrefix)
        {
            foreach (string line in GeneratedFileHeaderLines)
            { writer.WriteLine($"{linePrefix}{line}"); }
        }

        public delegate TWriter WriterFactory<TWriter>(OutputSession session, string filePath)
            where TWriter : class;

        public void AddFactory<TWriter>(WriterFactory<TWriter> factoryMethod)
            where TWriter : class
            => Factories.Add(typeof(TWriter), factoryMethod);

        private WriterFactory<TWriter> GetFactory<TWriter>()
            where TWriter : class
        {
            if (Factories.TryGetValue(typeof(TWriter), out Delegate? ret))
            { return (WriterFactory<TWriter>)ret; }

            // See if the type provides a factory method
            if (typeof(TWriter).GetCustomAttribute<ProvidesOutputSessionFactoryAttribute>() is not null)
            {
                const string factoryPropertyName = "FactoryMethod";
                PropertyInfo? factoryMethodProperty = typeof(TWriter).GetProperty
                (
                    factoryPropertyName,
                    BindingFlags.DoNotWrapExceptions | BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    typeof(WriterFactory<TWriter>),
                    Type.EmptyTypes,
                    Array.Empty<ParameterModifier>()
                );

                MethodInfo? factoryMethodGetter = factoryMethodProperty?.GetMethod;

                if (factoryMethodProperty is null || factoryMethodGetter is null)
                { throw new NotSupportedException($"{typeof(TWriter).FullName} is marked as providing a factory for {nameof(OutputSession)}, but it does not have a property matching the expected shape."); }

                WriterFactory<TWriter>? factory = (WriterFactory<TWriter>?)factoryMethodGetter.Invoke(null, null);

                if (factory is null)
                { throw new InvalidOperationException($"The {factoryPropertyName} property for {typeof(TWriter).FullName} returned null."); }

                Factories.Add(typeof(TWriter), factory);
                return factory;
            }

            // If there wasn't a value, throw
            throw new NotSupportedException($"This output session does not support creating {typeof(TWriter).FullName} instances.");
        }

        public TWriter Open<TWriter>(string filePath, WriterFactory<TWriter> factory)
            where TWriter : class
        {
            CheckDisposed();

            // Prefix the output directory if the path is relative
            if (!Path.IsPathRooted(filePath))
            { filePath = Path.Combine(BaseOutputDirectory, filePath); }

            // Normalize the file path
            filePath = Path.GetFullPath(filePath);

            // Handle duplicate file paths
            if (Writers.ContainsKey(filePath))
            {
                if (!AutoRenameConflictingFiles)
                { throw new InvalidOperationException($"Tried to create file ({filePath}) more than once."); }

                _FilesWrittenMoreThanOnce.Add(filePath);

                string prefix = Path.Combine(Path.GetDirectoryName(filePath)!, Path.GetFileNameWithoutExtension(filePath) + "_");
                string suffix = Path.GetExtension(filePath);
                int i = 0;
                do
                {
                    filePath = $"{prefix}{i}{suffix}";
                    i++;
                }
                while (Writers.ContainsKey(filePath));
            }

            // Ensure the containing directory exists
            string? fileDirectory = Path.GetDirectoryName(filePath);
            if (fileDirectory is not null && !Directory.Exists(fileDirectory))
            { Directory.CreateDirectory(fileDirectory); }

            // Create the writer
            TWriter writer = factory(this, filePath);
            Writers.Add(filePath, writer);
            return writer;
        }

        public TWriter Open<TWriter>(string filePath)
            where TWriter : class
            => Open(filePath, GetFactory<TWriter>());

        /// <summary>Copies the specified input file to the specified output file.</summary>
        /// <returns>The path the file was copied to.</returns>
        /// <remarks>If <see cref="AutoRenameConflictingFiles"/> is true, the file name of the returned path may the same as <paramref name="destinationFilePath"/>.</remarks>
        public string CopyFile(string sourceFilePath, string destinationFilePath)
        {
            ReserveWithoutOpening reservation = Open<ReserveWithoutOpening>(destinationFilePath);
            File.Copy(sourceFilePath, reservation.OutputPath);
            reservation.LockFile();
            return reservation.OutputPath;
        }

        /// <summary>Copies the specified source file to the output directory.</summary>
        /// <returns>The path the file was copied to.</returns>
        /// <remarks>If <see cref="AutoRenameConflictingFiles"/> is true, the file name of the returned path may the same as <paramref name="sourceFilePath"/>.</remarks>
        public string CopyFile(string sourceFilePath)
            => CopyFile(sourceFilePath, Path.GetFileName(sourceFilePath));

        private void ProcessAndUpdateFileLog()
        {
            CheckDisposed();
            string fileLogPath = Path.Combine(BaseOutputDirectory, "FilesWritten.txt");

            // Delete any files from previous output session that weren't written in this one
            if (File.Exists(fileLogPath))
            {
                using (StreamReader fileLog = new(fileLogPath))
                {
                    while (true)
                    {
                        string? filePath = fileLog.ReadLine();

                        if (filePath is null)
                        { break; }

                        filePath = Path.Combine(BaseOutputDirectory, filePath);

                        if (!Writers.ContainsKey(filePath))
                        { File.Delete(filePath); }
                    }
                }
            }

            // Write out a listing of all files written
            using (StreamWriter fileLog = new(fileLogPath))
            {
                foreach (string writtenFilePath in FilesWritten.OrderBy(f => f))
                {
                    // If the path is outside of the output directory, don't log it
                    if (!writtenFilePath.StartsWith(BaseOutputDirectory))
                    { continue; }

                    // Make the path relative to the output directory
                    string relativePath = Path.GetRelativePath(BaseOutputDirectory, writtenFilePath);

                    // Add the file to the log
                    fileLog.WriteLine(relativePath);
                }
            }
        }

        private bool IsDisposed = false;
        private void CheckDisposed()
        {
            if (IsDisposed)
            { throw new ObjectDisposedException(nameof(OutputSession)); }
        }

        public void Dispose()
        {
            if (IsDisposed)
            { return; }

            ProcessAndUpdateFileLog();

            // Dispose of all disposable writers
            foreach (object writer in Writers.Values)
            {
                if (writer is IDisposable disposable)
                { disposable.Dispose(); }
            }

            IsDisposed = true;
        }
    }
}