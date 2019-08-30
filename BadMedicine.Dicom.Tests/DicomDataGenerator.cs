﻿using BadMedicine.Datasets;
using Dicom;
using System;
using System.IO;
using System.Linq;
using System.Data;
using System.Collections.Generic;

namespace BadMedicine.Dicom
{
    public class DicomDataGenerator : DataGenerator
    {
        public DirectoryInfo OutputDir { get; }

        /// <summary>
        /// Set to true to generate <see cref="DicomDataset"/> without any pixel data.
        /// </summary>
        public bool NoPixels { get; set; }

        /// <summary>
        /// The subdirectories layout to put dicom files into when writting to disk
        /// </summary>
        public FileSystemLayout Layout{get {return _pathProvider.Layout; } set { _pathProvider = new FileSystemLayoutProvider(value);}}
        
        /// <summary>
        /// The maximum number of images to generate regardless of how many calls to <see cref="GenerateTestDataRow"/>,  Defaults to int.MaxValue
        /// </summary>
        public int MaximumImages { get; set; } = int.MaxValue;

        private FileSystemLayoutProvider _pathProvider = new FileSystemLayoutProvider(FileSystemLayout.StudyYearMonthDay);

        PixelDrawer drawing = new PixelDrawer();

        private int[] _modalities;

        private List<DicomTag> _studyTags;
        private List<DicomTag> _seriesTags;
        private List<DicomTag> _imageTags;
        private string _lastStudyUID = "";
        private string _lastSeriesUID = "";

        /// <summary>
        /// 
        /// </summary>
        /// <param name="r"></param>
        /// <param name="outputDir"></param>
        /// <param name="modalities">List of modalities to generate from e.g. CT,MR.  The frequency of images generated is based on
        /// the popularity of that modality in a clinical PACS.  Passing nothing results in all supported modalities being generated</param>
        public DicomDataGenerator(Random r, DirectoryInfo outputDir, params string[] modalities):base(r)
        {
            OutputDir = outputDir;
            
            var stats = DicomDataGeneratorStats.GetInstance(r);

            if(modalities.Length == 0)
            {
                _modalities = stats.ModalityIndexes.Values.ToArray();
            }
            else
            {
                foreach(var m in modalities)
                {
                    if(!stats.ModalityIndexes.ContainsKey(m))
                        throw new ArgumentException("Modality '" + m + "' was not supported, supported modalities are:" + string.Join(",",stats.ModalityIndexes.Select(kvp=>kvp.Key)));
                }

                _modalities = modalities.Select(m=>stats.ModalityIndexes[m]).ToArray();
            }

            InitialiseCsvOutput();
        }
        
        /// <summary>
        /// Creates a new dicom dataset
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public override object[] GenerateTestDataRow(Person p)
        {
            //The currently extracting study
            Study study;
            string studyUID = null;

            foreach(var ds in GenerateStudyImages(p, out study))
            {
                //don't generate more than the maximum number of images
                if(MaximumImages--<=0)
                {
                    study = null;
                    break; 
                } 
                else
                    studyUID = study.StudyUID.UID; //all images will have the same study

                var f = new DicomFile(ds);
            
                var fi = _pathProvider.GetPath(OutputDir,f.Dataset);
                if(!fi.Directory.Exists)
                    fi.Directory.Create();

                string fileName = fi.FullName;
                f.Save(fileName);

                // ACH : additions to produce some CSV data
                AddDicomDatasetToCsv(ds);
            }

            //in the CSV write only the StudyUID
            return new object[]{studyUID };
        }

        protected override string[] GetHeaders()
        {
            return new string[]{ "Studies Generated" };
        }

        /// <summary>
        /// Creates a dicom study for the <paramref name="p"/> with tag values that make sense for that person.  This call
        /// will generate an entire with a (sensible) random number of series and a random number of images per series
        /// (e.g. for CT studies you might get 2 series of ~100 images each).
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public DicomDataset[] GenerateStudyImages(Person p, out Study study)
        {        
            //generate a study
            study = new Study(this,p,GetRandomModality(),r);

            return study.SelectMany(series=>series).Select(image=>image).ToArray();
        }

        public DicomDataset GenerateTestDataset(Person p)
        {
            //get a random modality
            var modality = GetRandomModality();
            return GenerateTestDataset(p,new Study(this,p,modality,r).Series[0]);
        }

        private ModalityStats GetRandomModality()
        {
            return DicomDataGeneratorStats.GetInstance(r).ModalityFrequency.GetRandom(_modalities);
        }

        /// <summary>
        /// Returns a new random dicom image for the <paramref name="p"/> with tag values that make sense for that person
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public DicomDataset GenerateTestDataset(Person p,Series series)
        {
            var ds = new DicomDataset();
                        
            ds.AddOrUpdate(DicomTag.StudyInstanceUID,series.Study.StudyUID);
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID,series.SeriesUID);

            DicomUID sopInstanceUID = DicomUID.Generate();
            ds.AddOrUpdate(DicomTag.SOPInstanceUID,sopInstanceUID);
            ds.AddOrUpdate(DicomTag.SOPClassUID , DicomUID.SecondaryCaptureImageStorage);
            
            //patient details
            ds.AddOrUpdate(DicomTag.PatientID, p.CHI);
            ds.AddOrUpdate(DicomTag.PatientName, p.Forename + " " + p.Surname);
            ds.AddOrUpdate(DicomTag.PatientBirthDate, p.DateOfBirth);
            ds.AddOrUpdate(DicomTag.PatientAddress,p.Address.Line1 + " " + p.Address.Line2 + " " + p.Address.Line3 + " " + p.Address.Line4 + " " + p.Address.Postcode.Value);


            ds.AddOrUpdate(new DicomDate(DicomTag.StudyDate,series.Study.StudyDate));
            ds.AddOrUpdate(new DicomTime(DicomTag.StudyTime, DateTime.Today  +  series.Study.StudyTime));

            ds.AddOrUpdate(new DicomDate(DicomTag.SeriesDate,series.SeriesDate));
                        
            ds.AddOrUpdate(DicomTag.Modality,series.ModalityStats.Modality);
            
            if(series.Study.StudyDescription != null)
                ds.AddOrUpdate(DicomTag.StudyDescription,series.Study.StudyDescription);
                        


            // Calculate the age of the patient at the time the series was taken
            var age = series.SeriesDate.Year - p.DateOfBirth.Year;
            // Go back to the year the person was born in case of a leap year
            if (p.DateOfBirth.Date > series.SeriesDate.AddYears(-age)) age--;
                ds.AddOrUpdate(new DicomAgeString(DicomTag.PatientAge,age.ToString("000") + "Y"));
            
            if(!NoPixels)
                drawing.DrawBlackBoxWithWhiteText(ds,500,500,sopInstanceUID.UID);
            

            return ds;
        }

        // ACH - Methods for CSV output added below

        private void InitialiseCsvOutput()
        {
            // Write the headers

            _studyTags = new List<DicomTag>()
            {
                DicomTag.PatientID,
                DicomTag.StudyInstanceUID,
                DicomTag.StudyDate,
                DicomTag.StudyTime,
                DicomTag.ModalitiesInStudy,
                DicomTag.StudyDescription,
                DicomTag.PatientAge,     // TODO - needs converted into age
                DicomTag.NumberOfStudyRelatedInstances, // TODO - needs filled in
                DicomTag.PatientBirthDate
            };

            _seriesTags = new List<DicomTag>()
            {
                DicomTag.StudyInstanceUID,
                DicomTag.SeriesInstanceUID,
                DicomTag.Modality,
                DicomTag.SourceApplicationEntityTitle,
                DicomTag.InstitutionName,
                DicomTag.ProcedureCodeSequence,
                DicomTag.ProtocolName,
                DicomTag.PerformedProcedureStepID,
                DicomTag.PerformedProcedureStepDescription,
                DicomTag.SeriesDescription,
                DicomTag.BodyPartExamined,
                DicomTag.DeviceSerialNumber,
                DicomTag.NumberOfSeriesRelatedInstances,   // TODO - NEED TO FILL THIS IN 
                DicomTag.SeriesNumber
            };

            _seriesTags = new List<DicomTag>()
            {
                DicomTag.StudyInstanceUID,
                DicomTag.SeriesInstanceUID,
                DicomTag.Modality,
                DicomTag.SourceApplicationEntityTitle,
                DicomTag.InstitutionName,
                DicomTag.ProcedureCodeSequence,
                DicomTag.ProtocolName,
                DicomTag.PerformedProcedureStepID,
                DicomTag.PerformedProcedureStepDescription,
                DicomTag.SeriesDescription,
                DicomTag.BodyPartExamined,
                DicomTag.DeviceSerialNumber,
                DicomTag.NumberOfSeriesRelatedInstances,   // TODO - NEED TO FILL THIS IN 
                DicomTag.SeriesNumber
            };

            _imageTags = new List<DicomTag>()
            {
                DicomTag.SeriesInstanceUID,
                DicomTag.SOPInstanceUID,
                DicomTag.SeriesDate,
                DicomTag.SeriesTime,
                DicomTag.BurnedInAnnotation,
                DicomTag.SliceLocation,
                DicomTag.SliceThickness,
                DicomTag.SpacingBetweenSlices,
                DicomTag.SpiralPitchFactor,
                DicomTag.KVP,
                DicomTag.ExposureTime,
                DicomTag.Exposure,
                DicomTag.ImageType,
                DicomTag.ManufacturerModelName,
                DicomTag.Manufacturer,
                DicomTag.XRayTubeCurrent,
                DicomTag.PhotometricInterpretation,
                DicomTag.ContrastBolusRoute,
                DicomTag.ContrastBolusAgent,
                DicomTag.AcquisitionNumber,
                DicomTag.AcquisitionDate,
                DicomTag.AcquisitionTime,
                DicomTag.ImagePositionPatient,
                DicomTag.PixelSpacing,
                DicomTag.FieldOfViewDimensions,
                DicomTag.FieldOfViewDimensionsInFloat,
                DicomTag.DerivationDescription,
                DicomTag.TransferSyntaxUID,
                DicomTag.LossyImageCompression,
                DicomTag.LossyImageCompressionMethod,
                DicomTag.LossyImageCompressionRatio,
                DicomTag.ScanOptions
            };

            WriteData("STUDY>>", _studyTags.Select(i => i.DictionaryEntry.Keyword));
            WriteData("SERIES>>", _seriesTags.Select(i => i.DictionaryEntry.Keyword));
            WriteData("IMAGES>>", _imageTags.Select(i => i.DictionaryEntry.Keyword));
        }

        private void WriteData(string fileId, IEnumerable<string> data)
        {
            Console.WriteLine(fileId + String.Join("\t", data));
        }

        private void AddDicomDatasetToCsv(DicomDataset ds)
        {
            if (_lastStudyUID != ds.GetString(DicomTag.StudyInstanceUID))
            {
                _lastStudyUID = ds.GetString(DicomTag.StudyInstanceUID);

                WriteTags("STUDY>>", _studyTags, ds);
            }

            if (_lastSeriesUID != ds.GetString(DicomTag.SeriesInstanceUID))
            {
                _lastSeriesUID = ds.GetString(DicomTag.SeriesInstanceUID);

                WriteTags("SERIES>>", _seriesTags, ds);
            }

            WriteTags("IMAGE>>", _imageTags, ds);
        }

        private void WriteTags(string fileId, List<DicomTag> tags, DicomDataset ds)
        {
            var columnData = new List<string>();
            foreach (DicomTag tag in tags)
            {
                string value = "<MISSING>";  // TODO - change to "" later but easier to see with this

                if (ds.Contains(tag))
                {
                    value = ds.GetString(tag);
                }
                columnData.Add(value);
            }

            WriteData(fileId, columnData);
        }

    }
}
