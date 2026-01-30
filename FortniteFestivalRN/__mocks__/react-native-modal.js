const React = require('react');

function ReactNativeModalMock(props) {
  if (!props || !props.isVisible) return null;
  return React.createElement(React.Fragment, null, props.children);
}

module.exports = ReactNativeModalMock;
module.exports.default = ReactNativeModalMock;
