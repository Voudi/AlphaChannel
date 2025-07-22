#!/bin/bash
GIST="ff56526cfcd0197a87d518e07cf1302f"

git clone https://gist.github.com/Voudi/$GIST.git

cp AlphaChannel.zip $GIST/
cp pluginmaster.json $GIST/pluginmaster.json
cp AlphaChannel/images/icon.png $GIST/icon.png
cd $GIST
git show-ref 
git add AlphaChannel.zip
git add pluginmaster.json
git add icon.png

git commit -m "Adding Zip to Gist"
 
git push origin main
#If prompted, provide your Github credentials associated with the gist
cd ..
rm -rf $GIST
rm -rf AlphaChannel.zip
rm -rf pluginmaster.json