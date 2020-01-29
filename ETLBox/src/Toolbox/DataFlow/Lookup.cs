﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;


namespace ALE.ETLBox.DataFlow
{
    /// <summary>
    /// A lookup task - data from the input can be enriched with data retrieved from the lookup source.
    /// </summary>
    /// <typeparam name="TInput">Type of data input and output</typeparam>
    /// <typeparam name="TSourceOutput">Type of lookup data</typeparam>
    public class LookupTransformation<TInput, TSourceOutput>
        : DataFlowTransformation<TInput, TInput>, ITask, IDataFlowTransformation<TInput, TInput>
    {
        /* ITask Interface */
        public override string TaskName { get; set; } = "Lookup";

        public List<TSourceOutput> LookupList { get; set; }

        ActionBlock<TSourceOutput> LookupBuffer { get; set; }

        /* Public Properties */
        public override ISourceBlock<TInput> SourceBlock => RowTransformation.SourceBlock;
        public override ITargetBlock<TInput> TargetBlock => RowTransformation.TargetBlock;
        public IDataFlowSource<TSourceOutput> Source
        {
            get
            {
                return _source;
            }
            set
            {
                _source = value;
                Source.SourceBlock.LinkTo(LookupBuffer, new DataflowLinkOptions() { PropagateCompletion = true });
            }
        }
        public Func<TInput, TInput> RowTransformationFunc
        {
            get
            {
                return _rowTransformationFunc;
            }
            set
            {
                _rowTransformationFunc = value;
                RowTransformation = new RowTransformation<TInput, TInput>(this, _rowTransformationFunc);
                RowTransformation.InitAction = LoadLookupData;
            }
        }

        /* Private stuff */
        private RowTransformation<TInput, TInput> RowTransformation { get; set; }
        private Func<TInput, TInput> _rowTransformationFunc;
        private IDataFlowSource<TSourceOutput> _source;
        private TypeInfo TypeInfoInput { get; set; }
        private TypeInfo TypeInfoSource { get; set; }


        public LookupTransformation()
        {
            LookupBuffer = new ActionBlock<TSourceOutput>(row => FillBuffer(row));
            if (RowTransformationFunc == null)
            {
                TypeInfoInput = new TypeInfo(typeof(TInput));
                TypeInfoSource = new TypeInfo(typeof(TSourceOutput));
                RowTransformationFunc = new Func<TInput, TInput>(
                    row => {
                        var matchColumn = TypeInfoInput.GetInfoByPropertyNameOrColumnMapping("LookupId");
                        var retrieveColumn = TypeInfoInput.GetInfoByPropertyNameOrColumnMapping("LookupValue");
                        var matchColumnSource = TypeInfoSource.GetInfoByPropertyNameOrColumnMapping("Id");
                        var retrieveColumnSource = TypeInfoSource.GetInfoByPropertyNameOrColumnMapping("Value");
                        var matchValue = matchColumn.GetValue(row);
                        var lookupHit = LookupList.Find(e =>
                       {
                           return matchValue.Equals(matchColumnSource.GetValue(e));
                       });
                        var retrieveValue = retrieveColumnSource.GetValue(lookupHit);
                        retrieveColumn.SetValue(row, retrieveValue);
                        return row;
                    }
                );
            }
        }

        public LookupTransformation(Func<TInput, TInput> rowTransformationFunc, IDataFlowSource<TSourceOutput> source) : this()
        {
            RowTransformationFunc = rowTransformationFunc;
            Source = source;
        }

        public LookupTransformation(Func<TInput, TInput> rowTransformationFunc, IDataFlowSource<TSourceOutput> source, List<TSourceOutput> lookupList) : this()
        {
            RowTransformationFunc = rowTransformationFunc;
            Source = source;
            LookupList = lookupList;
        }

        private void LoadLookupData()
        {
            try
            {
                Source.Execute();
                LookupBuffer.Completion.Wait();
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        private void FillBuffer(TSourceOutput sourceRow)
        {
            if (LookupList == null) LookupList = new List<TSourceOutput>();
            LookupList.Add(sourceRow);
        }

        public void LinkLookupSourceErrorTo(IDataFlowLinkTarget<ETLBoxError> target) =>
            Source.LinkErrorTo(target);

        public void LinkLookupTransformationErrorTo(IDataFlowLinkTarget<ETLBoxError> target) =>
            RowTransformation.LinkErrorTo(target);
    }

    /// <summary>
    /// A lookup task - data from the input can be enriched with data retrieved from the lookup source.
    /// The non generic implementation accepts a string array as input and output. The lookup data source
    /// always returns a list of string array.
    /// </summary>
    /// <example>
    /// <code>
    /// Lookup = new Lookup(
    ///     testClass.TestTransformationFunc, lookupSource, testClass.LookupData
    /// );
    /// </code>
    /// </example>
    public class LookupTransformation : LookupTransformation<string[], string[]>
    {
        public LookupTransformation() : base()
        { }

        public LookupTransformation(Func<string[], string[]> rowTransformationFunc, IDataFlowSource<string[]> source)
            : base(rowTransformationFunc, source)
        { }

        public LookupTransformation(Func<string[], string[]> rowTransformationFunc, IDataFlowSource<string[]> source, List<string[]> lookupList)
            : base(rowTransformationFunc, source, lookupList)
        { }
    }

}
