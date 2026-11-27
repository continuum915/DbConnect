using System;
using System.Collections.Generic;

using TextMateSharp.Grammars;

namespace TextMateSharp.Model
{
    public abstract class AbstractLineList : IModelLines
    {
        private readonly List<ModelLine> _list = new List<ModelLine>();

        private TMModel _model;

        public AbstractLineList()
        {
        }

        public void SetModel(TMModel model)
        {
            this._model = model;
            lock(mLock)
            {
                foreach (var line in _list)
                {
                    line.IsInvalid = true;
                }
            }
        }

        public void AddLine(int line)
        {
            lock (mLock)
            {
                this._list.Insert(line, new ModelLine());
            }
        }

        public void RemoveLine(int line)
        {
            lock (mLock)
            {
                this._list.RemoveAt(line);
            }
        }

        // Update the storage once per document change rather than shifting the suffix per line.
        // The adapter publishes its snapshot while Get/ForEach are excluded by the same lock.
        protected void UpdateLineRange(int index, int removed, int added, Action publishSnapshot)
        {
            if (publishSnapshot == null)
                throw new ArgumentNullException(nameof(publishSnapshot));
            if (removed < 0)
                throw new ArgumentOutOfRangeException(nameof(removed));
            if (added < 0)
                throw new ArgumentOutOfRangeException(nameof(added));

            lock (mLock)
            {
                if (index < 0 || index > _list.Count - removed)
                    throw new ArgumentOutOfRangeException(nameof(index));

                var inserted = added == 0 ? Array.Empty<ModelLine>() : new ModelLine[added];
                for (int i = 0; i < added; i++)
                    inserted[i] = new ModelLine();

                _list.RemoveRange(index, removed);
                _list.InsertRange(index, inserted);
                publishSnapshot();
            }
        }

        public ModelLine Get(int index)
        {
            lock (mLock)
            {
                if (index < 0 || index >= this._list.Count)
                    return null;

                return this._list[index];
            }
        }

        public void ForEach(Action<ModelLine> action)
        {
            lock (mLock)
            {
                foreach (ModelLine modelLine in _list)
                    action(modelLine);
            }
        }

        protected void InvalidateLine(int lineIndex)
        {
            if (_model != null)
            {
                _model.InvalidateLine(lineIndex);
            }
        }

        protected void InvalidateLineRange(int iniLineIndex, int endLineIndex)
        {
            if (_model != null)
            {
                _model.InvalidateLineRange(iniLineIndex, endLineIndex);
            }
        }

        protected void ForceTokenization(int startLineIndex, int endLineIndex)
        {
            if (_model != null)
            {
                _model.ForceTokenization(startLineIndex, endLineIndex);
            }
        }

        public int GetSize()
        {
            return GetNumberOfLines();
        }

        public abstract void UpdateLine(int lineIndex);

        public abstract int GetNumberOfLines();

        public abstract LineText GetLineTextIncludingTerminators(int lineIndex);

        public abstract int GetLineLength(int lineIndex); 

        public abstract void Dispose(); 

        object mLock = new object();
    }
}