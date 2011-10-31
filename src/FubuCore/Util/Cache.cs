using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FubuCore.Util
{
    [Serializable]
    public class Cache<TKey, TValue> : IEnumerable<TValue>
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly IDictionary<TKey, TValue> _values;

        private Func<TValue, TKey> _getKey = delegate { throw new NotImplementedException(); };

        private Action<TValue> _onAddition = x => { };

        private Func<TKey, TValue> _onMissing = delegate(TKey key)
        {
            var message = string.Format("Key '{0}' could not be found", key);
            throw new KeyNotFoundException(message);
        };

        public Cache()
            : this(new Dictionary<TKey, TValue>())
        {
        }

        public Cache(Func<TKey, TValue> onMissing)
            : this(new Dictionary<TKey, TValue>(), onMissing)
        {
        }

        public Cache(IDictionary<TKey, TValue> dictionary, Func<TKey, TValue> onMissing)
            : this(dictionary)
        {
            _onMissing = onMissing;
        }

        public Cache(IDictionary<TKey, TValue> dictionary)
        {
            _values = dictionary;
        }

        public Action<TValue> OnAddition
        {
            set { _onAddition = value; }
        }

        public Func<TKey, TValue> OnMissing
        {
            set { _onMissing = value; }
        }

        public Func<TValue, TKey> GetKey
        {
            get { return _getKey; }
            set { _getKey = value; }
        }

        public int Count
        {
            get
            {
                return readLocked(() => _values.Count);
            }
        }

        public TValue First
        {
            get
            {
                return readLocked(() =>
                        {
                            foreach (var pair in _values)
                            {
                                return pair.Value;
                            }
                            return default(TValue);
                        });
            }
        }

        public TValue this[TKey key]
        {
            get
            {
                FillDefault(key);

                return readLocked(()=> _values[key]);
            }
            set
            {
                writeLocked(() =>
                                {
                                    _onAddition(value);
                                    if (_values.ContainsKey(key))
                                    {
                                        _values[key] = value;
                                    }
                                    else
                                    {
                                        _values.Add(key, value);
                                    }
                                });
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<TValue>) this).GetEnumerator();
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            return _values.Values.GetEnumerator();
        }

        /// <summary>
        ///   Guarantees that the Cache has the default value for a given key.
        ///   If it does not already exist, it's created.
        /// </summary>
        /// <param name = "key"></param>
        public void FillDefault(TKey key)
        {
            Fill(key, _onMissing);
        }

        public void Fill(TKey key, Func<TKey, TValue> onMissing)
        {
            conditionalWriteLock(() => !_values.ContainsKey(key), () =>
                {
                    var value = onMissing(key);
                    _onAddition(value);
                    _values.Add(key, value);
                });
        }

        public void Fill(TKey key, TValue value)
        {
            Fill(key, k => value);
        }

        public void Each(Action<TValue> action)
        {
            readLocked(() =>
                           {
                               foreach (var pair in _values)
                               {
                                   action(pair.Value);
                               }
                           });
        }

        public void Each(Action<TKey, TValue> action)
        {
            readLocked(() =>
                           {
                               foreach (var pair in _values)
                               {
                                   action(pair.Key, pair.Value);
                               }
                           });
        }

        public bool Has(TKey key)
        {
            return readLocked(() => _values.ContainsKey(key));
        }

        public bool Exists(Predicate<TValue> predicate)
        {
            return readLocked(() =>
                           {
                               var returnValue = false;

                               Each(delegate(TValue value) { returnValue |= predicate(value); });

                               return returnValue;
                           });
        }

        public TValue Find(Predicate<TValue> predicate)
        {
            return readLocked(() =>
                                  {
                                      foreach (var pair in _values)
                                      {
                                          if (predicate(pair.Value))
                                          {
                                              return pair.Value;
                                          }
                                      }

                                      return default(TValue);
                                  });
        }

        public TKey[] GetAllKeys()
        {
            return readLocked(() => _values.Keys.ToArray());
        }

        public TValue[] GetAll()
        {
            return readLocked(() => _values.Values.ToArray());
        }

        public void Remove(TKey key)
        {
            conditionalWriteLock(() => _values.ContainsKey(key), () => _values.Remove(key));
        }

        public void ClearAll()
        {
            writeLocked(()=>_values.Clear());
        }

        public bool WithValue(TKey key, Action<TValue> callback)
        {
            if( ! Has(key) ) return false;

            callback(this[key]);
            return true;
        }

        public IDictionary<TKey, TValue> ToDictionary()
        {
            return readLocked(() => new Dictionary<TKey, TValue>(_values));
        }

#region lock helper methods
        private T readLocked<T>(Func<T> func)
        {
            if (_lock.IsReadLockHeld) return func();

            _lock.EnterReadLock();
            try
            {
                return func();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private void readLocked(Action action)
        {
            readLocked<object>(() =>
            {
                action();
                return null;
            });
        }

        private T writeLocked<T>(Func<T> func)
        {
            if (_lock.IsWriteLockHeld)
            {
                return func();
            }

            // TODO: Check if( _lock.IsReadLockHeld )  ??? throw exception? Try to upgrade? What if it's not upgradeable?

            _lock.EnterWriteLock();
            try
            {
                return func();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void writeLocked(Action action)
        {
            writeLocked<object>(() =>
            {
                action();
                return null;
            });
        }

        private void conditionalWriteLock(Func<bool> condition, Action action)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (!condition()) return;

                _lock.EnterWriteLock();
                try
                {
                    action();
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
        }
#endregion
    }
}